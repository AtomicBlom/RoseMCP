using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Client;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// One live-app host process and the MCP client talking to it, the debugging counterpart to
/// <see cref="WorkspaceWorker"/>. The host owns one target process; closing its stdin (disposing the
/// client) is what stops it, the same mechanism that stops a host outliving a broker that dies.
/// </summary>
public sealed class LiveAppSession : IAsyncDisposable
{
	private static readonly JsonSerializerOptions SerializerOptions = McpJsonUtilities.DefaultOptions;
	private static readonly Dictionary<string, object?> EmptyArguments = [];

	private readonly McpClient _client;
	private readonly ActivityLog _activities;
	private readonly ILogger _logger;
	private LiveAppInfo? _info;
	private bool _alive = true;

	private LiveAppSession(
		string sessionId,
		LiveAppTarget target,
		TargetArchitecture architecture,
		McpClient client,
		ActivityLog activities,
		ILogger logger)
	{
		SessionId = sessionId;
		Target = target;
		Architecture = architecture;
		_client = client;
		_activities = activities;
		_logger = logger;
		StartedUtc = DateTime.UtcNow;
	}

	public string SessionId { get; }

	/// <summary>
	/// The MCP session that started this one, or null where the transport has no notion of one. Set by
	/// <see cref="LiveAppSessionManager"/>, which is the only thing that starts a session, so a session
	/// cannot exist without an owner having been decided for it.
	/// </summary>
	public string? Owner { get; internal set; }

	public LiveAppTarget Target { get; }

	/// <summary>The architecture the broker detected for the target and launched the host as.</summary>
	public TargetArchitecture Architecture { get; }

	public DateTime StartedUtc { get; }

	public int? HostProcessId => _info?.HostProcessId;

	/// <summary>
	/// Why the detach did not happen, once this session has been disposed of, and null when it did.
	/// <para>
	/// Read after <see cref="DisposeAsync"/> by whatever reports the close, because "the session is
	/// closed" and "the debugger is off your process" are two different claims and only the second
	/// one is what a caller detaching actually asked for.
	/// </para>
	/// </summary>
	public string? DetachFailure { get; private set; }

	public static async Task<LiveAppSession> StartAsync(
		string sessionId,
		LiveAppTarget target,
		TargetArchitecture architecture,
		string hostPath,
		ActivityLog activities,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		var logger = loggerFactory.CreateLogger<LiveAppSession>();

		var transport = new StdioClientTransport(
			new StdioClientTransportOptions
			{
				Command = hostPath,
				Arguments = BuildArguments(target),

				// Task Manager's Details tab shows this, which is how a human tells several hosts apart.
				Name = $"rose-live-app {target.Description}",
			},
			loggerFactory);

		logger.LogInformation("Starting a live-app host for {Target} as {Architecture}.", target.Description, architecture);

		var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);

		var session = new LiveAppSession(sessionId, target, architecture, client, activities, logger);
		await session.RefreshInfoAsync(cancellationToken);
		return session;
	}

	/// <summary>Re-reads the host's self-report. Cheap; the host loads nothing to answer it.</summary>
	public async Task RefreshInfoAsync(CancellationToken cancellationToken)
	{
		try
		{
			_info = await SendAsync<LiveAppInfo>(ToolNames.LiveAppInfo, cancellationToken);
		}
		catch (Exception exception)
		{
			_alive = false;
			_logger.LogDebug(exception, "Could not read live-app info for {Target}.", Target.Description);
		}
	}

	public LiveAppSessionSummary Describe()
	{
		var info = _info;
		var state = !_alive
			? LiveAppSessionState.Ended
			: info?.State ?? LiveAppSessionState.Starting;

		return new LiveAppSessionSummary
		{
			SessionId = SessionId,
			TargetDescription = Target.Description ?? Target.Kind.ToString(),
			Architecture = Architecture,
			State = state,
			HostProcessId = info?.HostProcessId,
			TargetProcessId = info?.TargetProcessId ?? Target.ProcessId,
			InstallLocation = info?.InstallLocation,
			StartedUtc = StartedUtc,
			Uptime = DateTime.UtcNow - StartedUtc,
			Detail = info?.Detail,
			Running = _activities.Running(SessionId),
			Recent = _activities.Recent(SessionId),
		};
	}

	private static List<string> BuildArguments(LiveAppTarget target)
	{
		var arguments = new List<string>();

		switch (target.Kind)
		{
			case LiveAppTargetKind.AttachProcess:
				arguments.Add("--attach");
				arguments.Add((target.ProcessId ?? 0).ToString());
				break;

			case LiveAppTargetKind.LaunchUwp:
				arguments.Add("--launch-uwp");
				arguments.Add(target.AppUserModelId ?? string.Empty);
				break;

			case LiveAppTargetKind.LaunchExecutable:
				arguments.Add("--launch");
				arguments.Add(target.ExecutablePath ?? string.Empty);
				break;
		}

		if (target.Arguments is { Length: > 0 } value)
		{
			arguments.Add("--arguments");
			arguments.Add(value);
		}

		if (target.Description is { Length: > 0 } description)
		{
			arguments.Add("--description");
			arguments.Add(description);
		}

		return arguments;
	}

	/// <summary>Reads the host's buffered debug events after the given cursor.</summary>
	public Task<LiveDebugEventPage> ReadEventsAsync(long after, CancellationToken cancellationToken)
		=> ReadEventsAsync(after, null, 500, 0, cancellationToken);

	/// <summary>Reads a page, narrowed to certain event kinds and capped in size.</summary>
	public Task<LiveDebugEventPage> ReadEventsAsync(long after, string[]? kinds, int limit, CancellationToken cancellationToken)
		=> ReadEventsAsync(after, kinds, limit, 0, cancellationToken);

	/// <summary>
	/// Reads a page, first waiting up to <paramref name="waitSeconds"/> for one event to be in it.
	/// <para>
	/// The wait happens in the host, which is the only place that knows when an event arrives, so this
	/// is an ordinary call that takes a while -- and it rides <c>SendAsync</c>, which cancels the far
	/// side before abandoning the wait. That ordering matters more here than anywhere else: a wait of
	/// half a minute abandoned locally would leave the host holding a reader for the rest of it.
	/// </para>
	/// </summary>
	public Task<LiveDebugEventPage> ReadEventsAsync(long after, string[]? kinds, int limit, int waitSeconds, CancellationToken cancellationToken)
		=> SendAsync<LiveDebugEventPage>(
			ToolNames.LiveAppEvents,
			new Dictionary<string, object?>
			{
				["after"] = after,
				["kinds"] = kinds,
				["limit"] = limit,
				["waitSeconds"] = waitSeconds,
			},
			cancellationToken);

	public Task<LiveTracepoint> AddTracepointAsync(string location, string? logMessage, int? logEveryNthHit, string? condition, CancellationToken cancellationToken)
		=> SendAsync<LiveTracepoint>(
			ToolNames.LiveAppAddTracepoint,
			new Dictionary<string, object?> { ["location"] = location, ["logMessage"] = logMessage, ["logEveryNthHit"] = logEveryNthHit, ["condition"] = condition },
			cancellationToken);

	public Task<LiveTracepointList> ListTracepointsAsync(CancellationToken cancellationToken)
		=> SendAsync<LiveTracepointList>(ToolNames.LiveAppListTracepoints, cancellationToken);

	public Task<LiveTracepointList> RemoveTracepointAsync(string id, CancellationToken cancellationToken)
		=> SendAsync<LiveTracepointList>(
			ToolNames.LiveAppRemoveTracepoint,
			new Dictionary<string, object?> { ["tracepointId"] = id },
			cancellationToken);

	public Task<LiveBreakpoint> SetBreakpointAsync(string location, int? autoContinueSeconds, string? condition, CancellationToken cancellationToken)
		=> SendAsync<LiveBreakpoint>(
			ToolNames.LiveAppSetBreakpoint,
			new Dictionary<string, object?> { ["location"] = location, ["autoContinueSeconds"] = autoContinueSeconds, ["condition"] = condition },
			cancellationToken);

	public Task<LiveBreakpointList> ListBreakpointsAsync(CancellationToken cancellationToken)
		=> SendAsync<LiveBreakpointList>(ToolNames.LiveAppListBreakpoints, cancellationToken);

	public Task<LiveBreakpointList> RemoveBreakpointAsync(string id, CancellationToken cancellationToken)
		=> SendAsync<LiveBreakpointList>(
			ToolNames.LiveAppRemoveBreakpoint,
			new Dictionary<string, object?> { ["breakpointId"] = id },
			cancellationToken);

	public async Task<bool> ContinueAsync(CancellationToken cancellationToken)
		=> (await SendAsync<LiveContinueResult>(ToolNames.LiveAppContinue, cancellationToken)).Continued;

	public async Task<bool> StepAsync(string mode, CancellationToken cancellationToken)
		=> (await SendAsync<LiveContinueResult>(
			ToolNames.LiveAppStep,
			new Dictionary<string, object?> { ["mode"] = mode },
			cancellationToken)).Continued;

	/// <summary>Evaluates a field-access expression against the stopped frame; runs no debuggee code.</summary>
	public Task<LiveEvaluation> EvaluateAsync(string expression, CancellationToken cancellationToken)
		=> SendAsync<LiveEvaluation>(
			ToolNames.LiveAppEvaluate,
			new Dictionary<string, object?> { ["expression"] = expression },
			cancellationToken);

	/// <summary>Injects the XAML provider into the target and reads a snapshot of its live visual tree.</summary>
	public Task<LiveXamlTree> ReadXamlTreeAsync(CancellationToken cancellationToken)
		=> ReadXamlTreeAsync(null, 0, 0, cancellationToken);

	/// <summary>
	/// Whether a reference is something the host cannot root at: an address or a handle rather than an
	/// <c>x:Name</c>. An address always carries a bracketed index, since it is a path of
	/// <c>Type[index]</c> segments, and a handle is all digits.
	/// </summary>
	private static bool Addressed(string root) =>
		root.Contains('[', StringComparison.Ordinal) || ulong.TryParse(root.Trim(), out _);

	/// <summary>An element and everything under it, in the order the whole tree reported them.</summary>
	private static IReadOnlyList<LiveXamlNode> Subtree(IReadOnlyList<LiveXamlNode> nodes, ulong root)
	{
		var kept = new HashSet<ulong> { root };

		// One pass is enough: the host reports a parent before its children, so whether a node's parent
		// is in the subtree is already settled by the time the node is looked at.
		return
		[
			.. nodes.Where(node =>
				kept.Contains(node.Handle) || (kept.Contains(node.Parent) && kept.Add(node.Handle))),
		];
	}

	/// <summary>
	/// Reads the tree, optionally rooted at one element and paged.
	/// <para>
	/// The host can root only at an <c>x:Name</c>, which is absent for everything inside a control
	/// template -- so anything else is resolved to a handle here and the subtree cut out of the whole
	/// tree. That costs the whole tree over the pipe, which is why a name still takes the host's own
	/// path: it is the cheap case and the common one.
	/// </para>
	/// </summary>
	public async Task<LiveXamlTree> ReadXamlTreeAsync(string? root, int offset, int limit, CancellationToken cancellationToken)
	{
		var name = root?.Trim().TrimStart('#');

		if (root is null || (name is { Length: > 0 } && !Addressed(root)))
		{
			return await SendAsync<LiveXamlTree>(
				ToolNames.LiveAppXamlTree,
				new Dictionary<string, object?> { ["rootName"] = name, ["offset"] = offset, ["limit"] = limit },
				cancellationToken);
		}

		var handle = await ResolveElementAsync(root, cancellationToken);
		var whole = await ReadXamlTreeAsync(cancellationToken);
		var subtree = Subtree(whole.Nodes, handle);

		var paged = subtree.Skip(offset);
		if (limit > 0) paged = paged.Take(limit);

		return whole with { Nodes = [.. paged], Total = subtree.Count };
	}

	/// <summary>Reads one element's XAML properties (by handle) with provenance and source location.</summary>
	public Task<LiveXamlProperties> ReadXamlPropertiesAsync(ulong handle, bool includeDefaults, CancellationToken cancellationToken)
		=> SendAsync<LiveXamlProperties>(
			ToolNames.LiveAppXamlProperties,
			new Dictionary<string, object?> { ["handle"] = handle, ["includeDefaults"] = includeDefaults },
			cancellationToken);

	/// <summary>Arms interactive select mode: the next click in the app picks that element.</summary>
	public Task<LiveXamlSelection> EnterXamlSelectModeAsync(bool includeAllElements, bool justMyXaml, CancellationToken cancellationToken)
		=> EnterXamlSelectModeAsync(includeAllElements, justMyXaml, arm: true, cancellationToken);

	/// <summary>
	/// Arms one of the overlay's pointer modes, or disarms whichever is on -- the toolbar's buttons,
	/// reached from here.
	/// </summary>
	public Task<LiveXamlSelection> EnterXamlSelectModeAsync(
		bool includeAllElements,
		bool justMyXaml,
		bool arm,
		CancellationToken cancellationToken,
		string mode = "select")
		=> SendAsync<LiveXamlSelection>(
			ToolNames.LiveAppXamlSelectMode,
			new Dictionary<string, object?>
			{
				["includeAllElements"] = includeAllElements,
				["justMyXaml"] = justMyXaml,
				["arm"] = arm,
				["mode"] = mode,
			},
			cancellationToken);

	/// <summary>Reads the element the user picked by clicking it in the running app.</summary>
	public Task<LiveXamlSelection> ReadXamlSelectionAsync(CancellationToken cancellationToken)
		=> SendAsync<LiveXamlSelection>(ToolNames.LiveAppXamlSelection, cancellationToken);

	/// <summary>Clears the picked element and the mark drawn over the running app.</summary>
	public Task<LiveXamlSelection> ClearXamlSelectionAsync(CancellationToken cancellationToken)
		=> SendAsync<LiveXamlSelection>(ToolNames.LiveAppXamlDeselect, cancellationToken);

	/// <summary>Selects the element a handle names, reaching what a click cannot.</summary>
	public Task<LiveXamlSelection> SelectXamlElementAsync(ulong handle, CancellationToken cancellationToken)
		=> SendAsync<LiveXamlSelection>(
			ToolNames.LiveAppXamlSelectElement,
			new Dictionary<string, object?> { ["handle"] = handle },
			cancellationToken);

	/// <summary>
	/// The handle an element reference names: a decimal handle, an <c>x:Name</c> with or without a
	/// leading <c>#</c>, or the address the tree and the selection report.
	/// <para>
	/// One parser, because a caller has all three forms to hand and could use only one at each tool.
	/// The tree and the selection both report a handle <em>and</em> an address, and the address is the
	/// form that exists for the elements that matter: everything inside a control template is unnamed,
	/// so a click usually lands on something whose only spoken name is its address -- and passing it
	/// back was refused for being not a number.
	/// </para>
	/// <para>
	/// A handle costs nothing to resolve. Anything else reads the tree first, because an address is a
	/// position among siblings and only the tree it came from can say which element that is. Refused
	/// rather than guessed at when nothing matches or several do: a duplicate <c>x:Name</c> is ordinary
	/// once a template is instantiated three times, and picking one of them is a guess wearing a
	/// success message.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">Nothing in the tree matches, or more than one thing does.</exception>
	public async Task<ulong> ResolveElementAsync(string element, CancellationToken cancellationToken)
	{
		if (ulong.TryParse(element.Trim(), out var handle)) return handle;

		var wanted = element.Trim();
		var tree = await ReadXamlTreeAsync(cancellationToken);

		var matching = tree.Nodes
			.Where(node => Names(node, wanted) || string.Equals(node.Address, wanted, StringComparison.Ordinal))
			.ToArray();

		if (matching.Length == 1) return matching[0].Handle;

		if (matching.Length > 1)
		{
			throw new ArgumentException(
				$"'{wanted}' names {matching.Length} elements in the live tree: "
					+ $"{string.Join(", ", matching.Select(node => node.Address ?? node.TypeName))}. "
					+ "A template instantiated more than once gives every x:Name in it that many elements, "
					+ "so pass one of those addresses, or the handle.");
		}

		throw new ArgumentException(
			$"Nothing in the live tree is called '{wanted}'. Pass a handle, an x:Name as #name, or the "
				+ "address rose_xaml_tree and rose_xaml_selection report -- which is what an element with no "
				+ "x:Name has instead.");
	}

	/// <summary>Whether a reference names this element, with or without the leading marker.</summary>
	private static bool Names(LiveXamlNode node, string wanted) =>
		node.Name is { Length: > 0 } name
		&& (string.Equals(name, wanted, StringComparison.Ordinal)
			|| string.Equals($"#{name}", wanted, StringComparison.Ordinal));

	/// <summary>
	/// Applies a XAML change to the live tree and returns each edit's outcome. Naming a file is the
	/// continuous path (#12) -- the host diffs it against what it last sent -- and two versions of the
	/// markup is for markup with no file behind it.
	/// </summary>
	public Task<LiveXamlApplyResult> ApplyXamlAsync(
		string? oldXaml,
		string? newXaml,
		string? filePath,
		CancellationToken cancellationToken)
		=> SendAsync<LiveXamlApplyResult>(
			ToolNames.LiveAppXamlApply,
			new Dictionary<string, object?> { ["filePath"] = filePath, ["oldXaml"] = oldXaml, ["newXaml"] = newXaml },
			cancellationToken);

	private Task<T> SendAsync<T>(string tool, CancellationToken cancellationToken)
		=> SendAsync<T>(tool, EmptyArguments, cancellationToken);

	private async Task<T> SendAsync<T>(
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		CancellationToken cancellationToken)
	{
		// The worker's twin: which live-app tools a session reaches for is worth being able to count.
		// A live-app session has an id of its own, so it says that as well as the origin directory.
		_logger.LogInformation(
			"Forwarding {Tool} to live-app session {SessionId} for {Origin}.",
			tool,
			SessionId,
			CallOrigin.Directory ?? "(no origin)");

		// Not CallToolAsync: it abandons the wait without telling the host, which then finishes the
		// work anyway. The same reasoning as the worker's SendAsync.
		var result = await CancellableToolCall.InvokeAsync(_client, tool, arguments, progress: null, cancellationToken);

		// Asked before the structured content, because a host that refused a call returns none -- so the
		// reason it refused was being replaced by "returned no structured content", which names the
		// consequence and not the cause.
		if (ForwardedError.Message(result) is { } failed) throw new InvalidOperationException(failed);

		if (result.StructuredContent is null)
		{
			throw new InvalidOperationException($"The live-app host returned no structured content for {tool}.");
		}

		return result.StructuredContent.Value.Deserialize<T>(SerializerOptions)
			?? throw new InvalidOperationException($"Could not read the live-app host's {tool} result.");
	}

	public async ValueTask DisposeAsync()
	{
		_alive = false;

		try
		{
			// Detach while the host is still alive, so the target is left running. An ICorDebug
			// debuggee whose debugger just dies is taken down with it, so this must precede closing
			// the host rather than relying on the host's own shutdown winning the race.
			var info = await SendAsync<LiveAppInfo>(ToolNames.LiveAppDetach, CancellationToken.None);

			// A host that could not detach reports Faulted rather than Ended, and that has to survive
			// disposal: a caller told the session closed and not told the debugger is still on their
			// process has been given the same silence this whole change exists to remove.
			if (info.State == LiveAppSessionState.Faulted)
			{
				DetachFailure = info.Detail ?? "The host could not detach from the target.";
				_logger.LogWarning(
					"The live-app host for {Target} could not detach: {Detail}", Target.Description, DetachFailure);
			}
		}
		catch (Exception exception)
		{
			DetachFailure = exception.Message;
			_logger.LogWarning(exception, "Detaching the live-app host for {Target} failed.", Target.Description);
		}

		try
		{
			// Disposing the client closes the host's stdin, which tells it to exit.
			await _client.DisposeAsync();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "The live-app host for {Target} did not shut down cleanly.", Target.Description);
		}
	}
}
