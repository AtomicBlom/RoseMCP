using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The registry of open live-app sessions, and the only place hosts are started or stopped. The
/// debugging counterpart to <see cref="WorkspaceManager"/>: a session is per running target, whereas
/// a worker is per solution, so the two are tracked separately even though they are supervised the
/// same way.
/// <para>
/// Registered as a singleton so sessions are shared across every connection, the way workers are.
/// </para>
/// </summary>
public sealed class LiveAppSessionManager(
	IOptions<BrokerOptions> options,
	ILoggerFactory loggerFactory,
	ILogger<LiveAppSessionManager> logger) : IAsyncDisposable
{
	private readonly Dictionary<string, LiveAppSession> _sessions = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly BrokerOptions _options = options.Value;

	/// <summary>What every session is doing, keyed by session id.</summary>
	public ActivityLog Activities { get; } = new();

	public IReadOnlyList<LiveAppSession> Sessions
	{
		get
		{
			lock (_sessions)
			{
				return [.. _sessions.Values];
			}
		}
	}

	/// <summary>One row per open session; the same model backs any UI and GET /admin/sessions.</summary>
	public IReadOnlyList<LiveAppSessionSummary> Describe() => [.. Sessions.Select(session => session.Describe())];

	public LiveAppSession? Find(string sessionId)
	{
		lock (_sessions)
		{
			return _sessions.GetValueOrDefault(sessionId);
		}
	}

	/// <summary>Starts a host against a target, detecting the target's architecture first.</summary>
	public async Task<LiveAppSession> StartAsync(LiveAppTarget target, CancellationToken cancellationToken)
	{
		var architecture = DetectArchitecture(target);
		var hostPath = LiveAppHostLauncher.ResolveHostPath(architecture, _options);
		var sessionId = NewSessionId();

		using var activity = Activities.Begin(sessionId, "start session", target.Description);
		try
		{
			var session = await LiveAppSession.StartAsync(
				sessionId, target, architecture, hostPath, Activities, loggerFactory, cancellationToken);

			await _gate.WaitAsync(cancellationToken);
			try
			{
				_sessions[sessionId] = session;
			}
			finally
			{
				_gate.Release();
			}

			logger.LogInformation("Live-app session {SessionId} started for {Target}.", sessionId, target.Description);
			return session;
		}
		catch (Exception exception)
		{
			activity.Complete(ActivityOutcome.Failed, exception.Message);
			throw;
		}
	}

	/// <summary>Stops a session's host and forgets it.</summary>
	public async Task<bool> CloseAsync(string sessionId, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!_sessions.Remove(sessionId, out var session)) return false;

			await session.DisposeAsync();
			Activities.Forget(sessionId);
			return true;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Which architecture to launch the host as. For an attach it is the target process's own; a
	/// classic UWP app is x64 (there is no ARM64 UWP runtime); a launched executable is the broker's
	/// own architecture until a launch path is built.
	/// </summary>
	private TargetArchitecture DetectArchitecture(LiveAppTarget target) => target switch
	{
		{ Kind: LiveAppTargetKind.AttachProcess, ProcessId: { } pid } => TargetArchitectureProbe.ForProcess(pid),
		{ Kind: LiveAppTargetKind.LaunchUwp } => TargetArchitecture.X64,
		_ => TargetArchitecture.Unknown,
	};

	private static string NewSessionId() => "session-" + Guid.NewGuid().ToString("N")[..8];

	public async ValueTask DisposeAsync()
	{
		foreach (var session in Sessions)
		{
			await session.DisposeAsync();
		}

		lock (_sessions)
		{
			_sessions.Clear();
		}

		_gate.Dispose();
	}
}
