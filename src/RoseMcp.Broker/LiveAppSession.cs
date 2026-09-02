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
		=> SendAsync<LiveDebugEventPage>(
			ToolNames.LiveAppEvents,
			new Dictionary<string, object?> { ["after"] = after },
			cancellationToken);

	public Task<LiveTracepoint> AddTracepointAsync(string location, string? logMessage, int? logEveryNthHit, CancellationToken cancellationToken)
		=> SendAsync<LiveTracepoint>(
			ToolNames.LiveAppAddTracepoint,
			new Dictionary<string, object?> { ["location"] = location, ["logMessage"] = logMessage, ["logEveryNthHit"] = logEveryNthHit },
			cancellationToken);

	public async Task<IReadOnlyList<LiveTracepoint>> ListTracepointsAsync(CancellationToken cancellationToken)
		=> (await SendAsync<LiveTracepointList>(ToolNames.LiveAppListTracepoints, cancellationToken)).Tracepoints;

	public async Task<IReadOnlyList<LiveTracepoint>> RemoveTracepointAsync(string id, CancellationToken cancellationToken)
		=> (await SendAsync<LiveTracepointList>(
			ToolNames.LiveAppRemoveTracepoint,
			new Dictionary<string, object?> { ["id"] = id },
			cancellationToken)).Tracepoints;

	public Task<LiveBreakpoint> SetBreakpointAsync(string location, int? autoContinueSeconds, CancellationToken cancellationToken)
		=> SendAsync<LiveBreakpoint>(
			ToolNames.LiveAppSetBreakpoint,
			new Dictionary<string, object?> { ["location"] = location, ["autoContinueSeconds"] = autoContinueSeconds },
			cancellationToken);

	public async Task<IReadOnlyList<LiveBreakpoint>> ListBreakpointsAsync(CancellationToken cancellationToken)
		=> (await SendAsync<LiveBreakpointList>(ToolNames.LiveAppListBreakpoints, cancellationToken)).Breakpoints;

	public async Task<IReadOnlyList<LiveBreakpoint>> RemoveBreakpointAsync(string id, CancellationToken cancellationToken)
		=> (await SendAsync<LiveBreakpointList>(
			ToolNames.LiveAppRemoveBreakpoint,
			new Dictionary<string, object?> { ["id"] = id },
			cancellationToken)).Breakpoints;

	public async Task<bool> ContinueAsync(CancellationToken cancellationToken)
		=> (await SendAsync<LiveContinueResult>(ToolNames.LiveAppContinue, cancellationToken)).Continued;

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
			await SendAsync<LiveAppInfo>(ToolNames.LiveAppDetach, CancellationToken.None);
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Detaching the live-app host for {Target} failed.", Target.Description);
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
