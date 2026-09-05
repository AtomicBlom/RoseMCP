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
	public Task<LiveDebugEventPage> ReadEventsAsync(long after, string? kinds, int limit, CancellationToken cancellationToken)
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
	public Task<LiveDebugEventPage> ReadEventsAsync(long after, string? kinds, int limit, int waitSeconds, CancellationToken cancellationToken)
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
			new Dictionary<string, object?> { ["id"] = id },
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
			new Dictionary<string, object?> { ["id"] = id },
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

	/// <summary>Reads the tree, optionally rooted at a named element and paged.</summary>
	public Task<LiveXamlTree> ReadXamlTreeAsync(string? rootName, int offset, int limit, CancellationToken cancellationToken)
		=> SendAsync<LiveXamlTree>(
			ToolNames.LiveAppXamlTree,
			new Dictionary<string, object?> { ["rootName"] = rootName, ["offset"] = offset, ["limit"] = limit },
			cancellationToken);

	/// <summary>Reads one element's XAML properties (by handle) with provenance and source location.</summary>
	public Task<LiveXamlProperties> ReadXamlPropertiesAsync(ulong handle, bool includeDefaults, CancellationToken cancellationToken)
		=> SendAsync<LiveXamlProperties>(
			ToolNames.LiveAppXamlProperties,
			new Dictionary<string, object?> { ["handle"] = handle, ["includeDefaults"] = includeDefaults },
			cancellationToken);

	/// <summary>Arms interactive select mode: the next click in the app picks that element.</summary>
	public Task<LiveXamlSelection> EnterXamlSelectModeAsync(bool includeAllElements, bool justMyXaml, CancellationToken cancellationToken)
		=> EnterXamlSelectModeAsync(includeAllElements, justMyXaml, arm: true, cancellationToken);

	/// <summary>Arms interactive select mode, or disarms it -- the toolbar's two buttons.</summary>
	public Task<LiveXamlSelection> EnterXamlSelectModeAsync(bool includeAllElements, bool justMyXaml, bool arm, CancellationToken cancellationToken)
		=> SendAsync<LiveXamlSelection>(
			ToolNames.LiveAppXamlSelectMode,
			new Dictionary<string, object?>
			{
				["includeAllElements"] = includeAllElements,
				["justMyXaml"] = justMyXaml,
				["arm"] = arm,
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
		// Not CallToolAsync: it abandons the wait without telling the host, which then finishes the
		// work anyway. The same reasoning as the worker's SendAsync.
		var result = await CancellableToolCall.InvokeAsync(_client, tool, arguments, progress: null, cancellationToken);

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
