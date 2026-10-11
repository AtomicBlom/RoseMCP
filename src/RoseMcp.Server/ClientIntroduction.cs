using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// What a relay knows of the session it serves, gathered for <see cref="RelayProtocol.ClientRoute"/>: the
/// client's roots, name and process, the branch it is on, and the label whatever started it gave it.
/// <para>
/// Learned from the client's requests rather than at connect, because the relay connects to the broker
/// before its own client has said anything: the client's name and capabilities arrive with its requests,
/// and on the current protocol revision with each request rather than once. The roots are asked of the
/// client from outside any request, bounded, and only where it declared it has roots; a client that does
/// not answer leaves the working directory to name the session, which is the folder a client starts a stdio
/// server in.
/// </para>
/// <para>
/// Nothing here throws for a fact it cannot find: a missing root, branch, label or process is a field left
/// empty, since an introduction is never a reason for anything the session asked to fail.
/// </para>
/// </summary>
public sealed class ClientIntroduction
{
	/// <summary>The environment variable whatever starts a session sets to label it -- with the task it was given, say.</summary>
	public const string LabelVariable = "ROSE_AGENT_LABEL";

	/// <summary>
	/// The notification a client sends when its roots change. Spelled out rather than taken from the SDK, which
	/// marks every part of roots obsolete since the newest revision deprecates them; see <see cref="ReadRootsAsync"/>.
	/// </summary>
	public const string RootsChangedNotification = "notifications/roots/list_changed";

	/// <summary>How long the client has to answer <c>roots/list</c>.</summary>
	private static readonly TimeSpan RootsBudget = TimeSpan.FromSeconds(5);

	private readonly string _session;
	private readonly string _workingDirectory;
	private readonly string? _label;
	private readonly Lazy<(int Id, string? Name)?> _process;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();

	/// <summary>The server the client's latest request arrived on. Guarded by <see cref="_gate"/>.</summary>
	private McpServer? _client;

	/// <summary>Whether the roots are to be asked for again: before the first time, after a failure, and after the client says they changed.</summary>
	private bool _rootsWanted = true;

	private IReadOnlyList<string> _roots = [];

	/// <param name="session">The name the relay sends with every call, which ties the calls to what this says.</param>
	/// <param name="workingDirectory">The directory the client started the relay in.</param>
	/// <param name="label">The label from <see cref="LabelVariable"/>, where it was set.</param>
	/// <param name="findProcess">Finds the client's process; asked once, when it is first needed.</param>
	/// <param name="logger">Where a client that would not say its roots is noted.</param>
	public ClientIntroduction(
		string session,
		string workingDirectory,
		string? label,
		Func<(int Id, string? Name)?> findProcess,
		ILogger logger)
	{
		_session = session;
		_workingDirectory = workingDirectory;
		_label = string.IsNullOrWhiteSpace(label) ? null : label;
		_process = new Lazy<(int Id, string? Name)?>(findProcess);
		_logger = logger;
	}

	/// <summary>Whether the client has made a request yet. Until it has there is nothing to introduce.</summary>
	public bool Known
	{
		get
		{
			lock (_gate)
			{
				return _client is not null;
			}
		}
	}

	/// <summary>
	/// Takes the server a request of the client's arrived on, the latest standing, since it carries what the
	/// client declared with that request. True for the first, which is when there is first something to say.
	/// </summary>
	public bool Learn(McpServer client)
	{
		lock (_gate)
		{
			var first = _client is null;
			_client = client;

			return first;
		}
	}

	/// <summary>Has the roots asked for again by the next gathering, as the client's <c>roots/list_changed</c> asks.</summary>
	public void RootsChanged()
	{
		lock (_gate)
		{
			_rootsWanted = true;
		}
	}

	/// <summary>
	/// Everything there is to say now, asking the client for its roots where they are wanted.
	/// </summary>
	/// <param name="heldTools">The fingerprint of what the client holds.</param>
	/// <param name="toolsCurrent">Whether that is what the broker offers.</param>
	/// <param name="cancellationToken">Ends the asking, when the relay does.</param>
	public async Task<RelayClient> GatherAsync(string? heldTools, bool? toolsCurrent, CancellationToken cancellationToken)
	{
		McpServer? client;
		bool rootsWanted;

		lock (_gate)
		{
			client = _client;
			rootsWanted = _rootsWanted;
			_rootsWanted = false;
		}

		if (rootsWanted && client is not null)
		{
			var roots = await ReadRootsAsync(client, cancellationToken);

			lock (_gate)
			{
				if (roots is null) _rootsWanted = true;
				else _roots = roots;
			}
		}

		IReadOnlyList<string> known;
		lock (_gate)
		{
			known = _roots;
		}

		var process = _process.Value;

		return new RelayClient
		{
			Session = _session,
			WorkingDirectory = _workingDirectory,
			Roots = known,
			Branch = GitBranch.Of(known.Count > 0 ? known[0] : _workingDirectory),
			ClientProcessId = process?.Id,
			ClientProcessName = process?.Name,
			ClientName = client?.ClientInfo?.Name,
			ClientVersion = client?.ClientInfo?.Version,
			Label = _label,
			HeldTools = heldTools,
			ToolsCurrent = toolsCurrent,
		};
	}

	/// <summary>A root's URI as a local path where it is a file URI, else as the client wrote it.</summary>
	public static string PathOf(string uri) =>
		Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;

	/// <summary>
	/// The client's roots; none where it declared it has none; null where it would not say, so they are asked for again.
	/// <para>
	/// The 2026-07-28 revision deprecates roots, and the SDK marks every part of them obsolete. A client on an
	/// earlier revision still declares and answers them, and they are the only way to learn a session's other
	/// folders, so they are asked for wherever a client says it has them; one that does not is named by its
	/// working directory, which is the folder it started the relay in.
	/// </para>
	/// </summary>
	private async Task<IReadOnlyList<string>?> ReadRootsAsync(McpServer client, CancellationToken cancellationToken)
	{
#pragma warning disable MCP9005 // Roots are deprecated by the newest revision and still answered by clients on the ones before it.
		if (client.ClientCapabilities?.Roots is null) return [];

		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		bounded.CancelAfter(RootsBudget);

		try
		{
			var listed = await client.RequestRootsAsync(new ListRootsRequestParams(), bounded.Token);

			return [.. listed.Roots.Select(root => PathOf(root.Uri))];
		}
		catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
		{
			_logger.LogDebug(exception, "The client did not say its roots, so its session is named by the directory it started in.");
			return null;
		}
#pragma warning restore MCP9005
	}
}
