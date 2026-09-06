using System.Collections.Concurrent;

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
	/// <summary>
	/// The open sessions, by session id.
	/// <para>
	/// Concurrent because the readers and the writer are not the same caller: Find runs on every debug
	/// tool call while StartAsync may be inserting, and a plain Dictionary read against a concurrent
	/// write is documented to throw or to corrupt its table. The gate below is a different guarantee --
	/// it makes a close atomic with the host teardown it entails.
	/// </para>
	/// </summary>
	private readonly ConcurrentDictionary<string, LiveAppSession> _sessions = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly BrokerOptions _options = options.Value;

	/// <summary>What every session is doing, keyed by session id.</summary>
	public ActivityLog Activities { get; } = new();

	public IReadOnlyList<LiveAppSession> Sessions => [.. _sessions.Values];

	/// <summary>One row per open session; the same model backs any UI and GET /admin/sessions.</summary>
	public IReadOnlyList<LiveAppSessionSummary> Describe() => [.. Sessions.Select(session => session.Describe())];

	/// <summary>
	/// One row per session the caller owns, which is what an agent-facing list may show.
	/// <see cref="Describe"/> is the whole picture, for the tray window and GET /admin/sessions, where
	/// the reader is the person running the broker rather than one of its clients.
	/// <para>
	/// Scoped for the same reason <see cref="Find"/> is, and because the refusal there sends the caller
	/// here: a list naming sessions it cannot then use would be worse than no list.
	/// </para>
	/// </summary>
	public IReadOnlyList<LiveAppSessionSummary> DescribeOwned() =>
		[.. Sessions.Where(Owns).Select(session => session.Describe())];

	/// <summary>
	/// Whether this call may reach that session. A stdio broker records no owner and matches null with
	/// null, since the process has one session for its whole life and nothing else can reach it.
	/// </summary>
	private static bool Owns(LiveAppSession session) =>
		string.Equals(session.Owner, CallSession.Id, StringComparison.Ordinal);

	/// <summary>
	/// The session with that id, if the caller owns it.
	/// <para>
	/// A live-app session is a debugger attached to somebody's running program: its events carry
	/// captured exceptions and log output, and its other tools set breakpoints, evaluate expressions
	/// and edit the running UI. The broker is a singleton every connection shares, so without this an
	/// http client reaches another client's target by guessing an eight-character id -- or by reading
	/// GET /admin/sessions, which lists them.
	/// </para>
	/// <para>
	/// Refused as though it were not there, rather than as a session belonging to someone else,
	/// because which of those it is is not the caller's business and the next step is the same either
	/// way. A stdio broker records no owner and every call in it matches, since the process has one
	/// session for its whole life.
	/// </para>
	/// </summary>
	public LiveAppSession? Find(string sessionId)
	{
		if (!_sessions.TryGetValue(sessionId, out var session)) return null;

		return Owns(session) ? session : null;
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

			// Recorded before it is reachable, so there is no window in which a session exists with no
			// owner and every caller is its owner.
			session.Owner = CallSession.Id;

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

	/// <summary>
	/// Stops a session's host and forgets it, if the caller owns it. Detaching someone else's debugger
	/// is the loudest thing this surface can do to another client, so it goes through the same
	/// ownership check as every read.
	/// </summary>
	public async Task<bool> CloseAsync(string sessionId, CancellationToken cancellationToken)
	{
		if (Find(sessionId) is null) return false;

		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!_sessions.TryRemove(sessionId, out var session)) return false;

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
	/// Which architecture to launch the host as. For an attach it is the target process's own; for a
	/// launched executable it is read from the executable's PE header; a classic UWP app is x64 (there
	/// is no ARM64 UWP runtime). Unknown falls back to the broker's own architecture in the launcher.
	/// </summary>
	private TargetArchitecture DetectArchitecture(LiveAppTarget target) => target switch
	{
		{ Kind: LiveAppTargetKind.AttachProcess, ProcessId: { } pid } => TargetArchitectureProbe.ForProcess(pid),
		{ Kind: LiveAppTargetKind.LaunchExecutable, ExecutablePath: { } path } => TargetArchitectureProbe.ForExecutable(path),
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

		_sessions.Clear();

		_gate.Dispose();
	}
}
