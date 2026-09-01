using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoseMcp.Broker;

namespace RoseMcp.Server;

/// <summary>
/// Forwards this session's tool calls to a tray that is already running, instead of starting
/// workers of our own.
/// <para>
/// The tray hosts a broker and serves MCP over http, so delegating to it needs no new protocol:
/// this is an MCP client of an endpoint that already exists. What it buys is the pair of things
/// neither transport gives alone -- one warm worker per solution shared by every session, because
/// the tray owns them all, and a correct answer to "which workspace did you mean", because a stdio
/// process knows the directory its client started it in and an http broker never can.
/// </para>
/// <para>
/// The tools are not redeclared here. Listing is forwarded too, so the surface cannot drift from
/// the tray's.
/// </para>
/// </summary>
public sealed class TrayRelay(McpClient tray, string workingDirectory, ILogger logger) : IAsyncDisposable
{
	private const string WorkspaceArgument = "workspace";

	/// <summary>
	/// Connects to a tray if one is listening, or returns null so the caller can run its own broker.
	/// <para>
	/// A short timeout on purpose: this runs before the session can do anything, and a tray that is
	/// slow to answer is indistinguishable from one that is not there. Being wrong here costs a
	/// private set of workers, not correctness.
	/// </para>
	/// </summary>
	public static async Task<TrayRelay?> TryConnectAsync(
		ServerOptions options,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		var logger = loggerFactory.CreateLogger<TrayRelay>();
		var endpoint = new Uri($"http://{options.Host}:{options.Port}/");

		try
		{
			using var probing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			probing.CancelAfter(TimeSpan.FromSeconds(2));

			var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint });
			var client = await McpClient.CreateAsync(
				transport, loggerFactory: loggerFactory, cancellationToken: probing.Token);

			logger.LogInformation("Relaying to the tray at {Endpoint}; its workers are shared.", endpoint);

			return new TrayRelay(client, Environment.CurrentDirectory, logger);
		}
		catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			logger.LogInformation(
				"No tray at {Endpoint} ({Reason}); this session will own its workers.", endpoint, exception.Message);

			return null;
		}
	}

	public async ValueTask<ListToolsResult> ListToolsAsync(CancellationToken cancellationToken)
	{
		var tools = await tray.ListToolsAsync(cancellationToken: cancellationToken);

		return new ListToolsResult { Tools = [.. tools.Select(tool => tool.ProtocolTool)] };
	}

	public async ValueTask<CallToolResult> CallToolAsync(
		CallToolRequestParams request,
		IProgress<ProgressNotificationValue>? progress,
		CancellationToken cancellationToken)
	{
		var arguments = WithWorkspace(request.Arguments);

		return await tray.CallToolAsync(
			request.Name,
			arguments,
			progress,
			cancellationToken: cancellationToken);
	}

	public ValueTask DisposeAsync() => tray.DisposeAsync();

	/// <summary>
	/// Fills in the workspace a call left out, from the directory this process was started in.
	/// <para>
	/// This is the whole reason the relay is worth having. The tray serves every repository on the
	/// machine, so with two solutions open it cannot guess which one a bare call means and has to
	/// refuse. A stdio process cannot be ambiguous: its client chose its working directory. Filling
	/// the argument in here means the tray is never asked the unanswerable question.
	/// </para>
	/// <para>
	/// Only when the directory actually resolves. A session started above a solution, or somewhere
	/// with none at all, is better served by the tray's own inference than by an argument naming a
	/// directory that means nothing.
	/// </para>
	/// </summary>
	private IReadOnlyDictionary<string, object?> WithWorkspace(IDictionary<string, JsonElement>? arguments)
	{
		var supplied = arguments?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
			?? [];

		var alreadyThere = arguments is not null
			&& arguments.TryGetValue(WorkspaceArgument, out var existing)
			&& existing.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
			&& !string.IsNullOrWhiteSpace(existing.ToString());

		if (alreadyThere) return supplied;

		if (!TryResolveWorkingDirectory(out var resolved)) return supplied;

		supplied[WorkspaceArgument] = resolved;
		logger.LogDebug("Filled in workspace {Workspace} from the working directory.", resolved);

		return supplied;
	}

	private bool TryResolveWorkingDirectory(out string? resolved)
	{
		try
		{
			resolved = SolutionResolver.Resolve(workingDirectory);
			return true;
		}
		catch (ArgumentException)
		{
			resolved = null;
			return false;
		}
	}
}
