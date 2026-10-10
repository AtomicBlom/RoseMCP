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

	/// <summary>Sessions dropped because their host died, so a later call naming one is told why.</summary>
	private readonly DroppedSessions _dropped = new();

	/// <summary>
	/// How often every session's self-report is re-read. It is the tray's own idle cadence, and it is
	/// what makes a summary's staleness bounded rather than unknown: nothing else asks a host how it
	/// is between tool calls, so without this a session sat at whatever it last said -- for a session
	/// nobody is calling, that is the whole time it exists.
	/// </summary>
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

	/// <summary>
	/// How long one poll may take before it is abandoned. Short, because the answer is cheap and a
	/// host that will not give it in this long is telling us something the next tick will ask again.
	/// A poll that expires does not mark the session dead -- only a transport failure does.
	/// </summary>
	private static readonly TimeSpan RefreshBudget = TimeSpan.FromSeconds(2);

	/// <summary>
	/// The poll in flight per session, so a host slower than the interval does not accumulate a queue
	/// of them. One tick skips a session that is still answering the last.
	/// </summary>
	private readonly ConcurrentDictionary<string, Task> _refreshes = new(StringComparer.Ordinal);

	private readonly CancellationTokenSource _stopping = new();

	private Task? _refreshing;

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

	/// <summary>
	/// The session with that id, whoever started it, or null when there is no such session.
	/// <para>
	/// It exists beside <see cref="Find"/> because the two have different callers with different
	/// standing. <see cref="Find"/> serves an MCP client, which may reach only the sessions it started;
	/// this serves the person running the broker, reading a window on their own machine or an operator
	/// endpoint behind a token that no client is given.
	/// </para>
	/// <para>
	/// It is not <see cref="Find"/> with the check relaxed, and must not become that. Inside an http
	/// endpoint <see cref="CallSession.Id"/> is null, because the filter that sets it runs for tool
	/// calls only -- so <see cref="Find"/> compares null against the owner recorded when the session
	/// started and refuses every session an agent has, which is all of them. Relaxing the check
	/// instead would hand one client another's debugger.
	/// </para>
	/// </summary>
	public LiveAppSession? ForOperator(string sessionId) =>
		_sessions.TryGetValue(sessionId, out var session) ? session : null;

	/// <summary>
	/// The session with that id that was dropped because its host died, if the caller started it, or
	/// null. For a refusal that would otherwise say only that no such session is open -- which, for a
	/// session the caller did start, reads as a wrong id or someone else's session.
	/// </summary>
	public DroppedSession? FindDropped(string sessionId) => _dropped.Find(sessionId, CallSession.Id);

	/// <summary>
	/// The session with that id that was dropped because its host died, whoever started it. The
	/// operator counterpart to <see cref="FindDropped"/>, for the reason <see cref="ForOperator"/> is.
	/// </summary>
	public DroppedSession? DroppedForOperator(string sessionId) => _dropped.ForOperator(sessionId);

	/// <summary>
	/// Stops a session and forgets it, whoever started it. The operator counterpart to
	/// <see cref="CloseAsync"/>, for a person detaching a debugger from their own machine.
	/// </summary>
	public async Task<bool> CloseForOperatorAsync(string sessionId, CancellationToken cancellationToken)
	{
		if (ForOperator(sessionId) is null) return false;

		return await RemoveAsync(sessionId, cancellationToken);
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

				// Under the same gate, so the first session both registers itself and starts the poll
				// that keeps every session's report fresh, with no window where one has happened and
				// the other has not.
				EnsureRefreshing();
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

		return await RemoveAsync(sessionId, cancellationToken);
	}

	/// <summary>
	/// Takes a session out of the registry and ends its host, having already established that the
	/// caller may. Shared by both closes and by dropping an ended session, so the teardown cannot differ
	/// between them: the gate is what makes removing the session atomic with disposing the host it names.
	/// <para>
	/// A drop passes when the host was found gone, and the session is remembered as dropped under the
	/// same gate it leaves the registry under -- so there is no moment at which a call naming it finds
	/// neither the session nor the reason it went.
	/// </para>
	/// </summary>
	private async Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken, DateTime? hostGoneUtc = null)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!_sessions.TryRemove(sessionId, out var session)) return false;

			if (hostGoneUtc is { } gone)
			{
				_dropped.Record(new DroppedSession(
					sessionId, session.Owner, session.Target.Description ?? session.Target.Kind.ToString(), gone));
			}

			await session.DisposeAsync();
			Activities.Forget(sessionId);
			_refreshes.TryRemove(sessionId, out _);
			return true;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Starts the poll that keeps every session's self-report fresh, if it is not already running.
	/// Called under the gate by the only thing that adds a session, so the check and the start cannot
	/// interleave and produce two loops.
	/// </summary>
	private void EnsureRefreshing() => _refreshing ??= Detached.Run(() => RefreshLoopAsync(_stopping.Token));

	/// <summary>
	/// Re-reads every session's self-report on a timer, for as long as this manager lives, and drops the
	/// sessions whose host has gone.
	/// <para>
	/// Here rather than in whatever is displaying the sessions, because there is more than one such
	/// reader -- a window, an admin endpoint, an operator API, in two different hosts -- and a poll
	/// belonging to one of them would leave the others reading whatever it happened to have fetched.
	/// A broker with no sessions polls nothing; the loop iterates the registry.
	/// </para>
	/// <para>
	/// Dropping runs on this loop, awaited, before the tick's polls start, and nothing else starts a
	/// poll -- so a dropped session cannot be polled again by the tick that dropped it, and the
	/// <see cref="DisposeAsync"/> that waits for this loop waits for a drop in progress too. One tick
	/// failing does not end the loop, for the reason the worker sweep gives: a broker that silently
	/// stopped dropping ended sessions after one bad tick would collect them for the rest of its life.
	/// </para>
	/// </summary>
	private async Task RefreshLoopAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(RefreshInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				try
				{
					await DropEndedAsync(cancellationToken);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					logger.LogWarning(exception, "Dropping ended live-app sessions failed; the next tick tries again.");
				}

				PollAll(cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			// The manager is going away. Nothing to report: the sessions go with it.
		}
	}

	/// <summary>
	/// Starts one poll per session whose host answers and which is not still answering the last.
	/// <para>
	/// A session whose host has gone is not asked: it can only fail the same way again, and asking it
	/// every second until it is dropped is the cost dropping exists to end.
	/// </para>
	/// </summary>
	private void PollAll(CancellationToken cancellationToken)
	{
		foreach (var session in Sessions)
		{
			if (!session.IsAlive) continue;

			var busy = _refreshes.TryGetValue(session.SessionId, out var running) && !running.IsCompleted;
			if (busy) continue;

			_refreshes[session.SessionId] = RefreshOneAsync(session, cancellationToken);

			// A caller's close between the snapshot above and here has already cleared its entry, so the
			// one just written would outlive the session. Cleared again: the close takes the session out
			// of the registry before it clears the entry, so one of the two always sees the other.
			if (!_sessions.ContainsKey(session.SessionId)) _refreshes.TryRemove(session.SessionId, out _);
		}
	}

	/// <summary>
	/// Marks every session whose host has just been found gone, and drops every one gone past
	/// <see cref="BrokerOptions.EndedSessionGrace"/>.
	/// <para>
	/// Marking files the reason on the session's own row, which stays listed as ended for the grace
	/// period, so the tray, an inspector and <c>rose_debug_list</c> say why before the row goes. The
	/// note goes with the session, so the drop itself is said in the broker's log, and the session is
	/// remembered as dropped, so a later call naming it is told its host died rather than that no such
	/// session is open.
	/// </para>
	/// <para>
	/// A session with a poll still settling is left to the next tick, so its client is never disposed
	/// under a call in flight on it. Dropping goes through <see cref="RemoveAsync"/>, the same teardown
	/// as a close, under the same gate: a caller closing the session at the same moment finds it gone
	/// or takes it first, and either way it is ended once.
	/// </para>
	/// </summary>
	private async Task DropEndedAsync(CancellationToken cancellationToken)
	{
		var grace = _options.EndedSessionGrace;

		foreach (var session in Sessions)
		{
			var now = UtcNow;
			var verdict = EndedSessionEviction.Decide(session.IsAlive, session.EndedSeenUtc, grace, now);

			if (verdict == EndedSessionVerdict.MarkEnded)
			{
				await MarkEndedAsync(session, now, grace, cancellationToken);
				continue;
			}

			if (verdict != EndedSessionVerdict.Drop) continue;

			var polling = _refreshes.TryGetValue(session.SessionId, out var running) && !running.IsCompleted;
			if (polling) continue;

			if (!await RemoveAsync(session.SessionId, cancellationToken, session.EndedSeenUtc ?? now)) continue;

			logger.LogInformation(
				"Dropped live-app session {SessionId} for {Target}: its host stopped answering {Ago} ago.",
				session.SessionId,
				session.Target.Description,
				WorkerEviction.Duration(now - (session.EndedSeenUtc ?? now)));
		}
	}

	/// <summary>
	/// Records that a session's host has gone, and files the reason on its row, if the session is still
	/// registered.
	/// <para>
	/// Under the gate a close takes, because a close also leaves its session not alive: one that ran
	/// between this tick's snapshot and here has already forgotten the session's activities, and a note
	/// filed after that would sit in the log under an id nothing lists, with a line in the broker's log
	/// blaming a host that was in fact closed on purpose.
	/// </para>
	/// </summary>
	private async Task MarkEndedAsync(LiveAppSession session, DateTime now, TimeSpan grace, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var stillRegistered = _sessions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, session);
			if (!stillRegistered) return;

			session.EndedSeenUtc = now;
			Activities.Note(session.SessionId, DropOperation, EndedSessionEviction.Explain(grace));
		}
		finally
		{
			_gate.Release();
		}

		logger.LogInformation(
			"The host of live-app session {SessionId} for {Target} stopped answering; the session is dropped in {Grace}.",
			session.SessionId,
			session.Target.Description,
			WorkerEviction.Duration(grace));
	}

	/// <summary>
	/// Now, on the clock an ended session's grace is read from -- and the one a refusal reads to say
	/// how long ago a dropped session's host went.
	/// </summary>
	public DateTime UtcNow => _options.TimeProvider.GetUtcNow().UtcDateTime;

	/// <summary>How a session found ended is labelled in the activity log, beside the calls it served.</summary>
	public const string DropOperation = "drop session";

	/// <summary>
	/// One session's poll, bounded and swallowing its own failure.
	/// <para>
	/// Bounded per session rather than per sweep, so one host that has stopped answering does not stop
	/// the others being asked. Swallowing, because this is nobody's call: a failure has already been
	/// recorded on the session, and there is no caller here to throw at.
	/// </para>
	/// </summary>
	private static async Task RefreshOneAsync(LiveAppSession session, CancellationToken cancellationToken)
	{
		try
		{
			using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			budget.CancelAfter(RefreshBudget);

			await session.RefreshInfoAsync(budget.Token);
		}
		catch (Exception)
		{
			// RefreshInfoAsync records what it learned; anything past it belongs to no caller.
		}
	}

	/// <summary>
	/// Which architecture to launch the host as. Every case asks rather than assumes: an attach reads
	/// the target process, a launched executable its PE header, and a packaged app its registered
	/// package identity. Unknown falls back to the broker's own architecture in the launcher.
	/// </summary>
	private TargetArchitecture DetectArchitecture(LiveAppTarget target) => target switch
	{
		{ Kind: LiveAppTargetKind.AttachProcess, ProcessId: { } pid } => TargetArchitectureProbe.ForProcess(pid),
		{ Kind: LiveAppTargetKind.LaunchExecutable, ExecutablePath: { } path } => TargetArchitectureProbe.ForExecutable(path),
		{ Kind: LiveAppTargetKind.LaunchUwp, AppUserModelId: { } aumid } => UwpArchitecture(aumid),
		_ => TargetArchitecture.Unknown,
	};

	/// <summary>
	/// The architecture to debug a packaged app as: what its package identity says, and x64 when that
	/// says nothing.
	/// <para>
	/// The fallback is x64 rather than Unknown because Unknown means the broker's own architecture,
	/// and for this one target kind that is the wrong guess on the machine where it matters. A package
	/// registered as <c>neutral</c> carries no architecture of its own, and a modern UWP app always
	/// carries one -- so neutral means classic UWP, which has no ARM64 runtime and runs x64 under
	/// emulation. Falling through to the broker's architecture would pick an ARM64 host for it on an
	/// ARM64 machine, which is the one thing the old hard-coded x64 got right.
	/// </para>
	/// </summary>
	private static TargetArchitecture UwpArchitecture(string appUserModelId)
	{
		var architecture = TargetArchitectureProbe.ForPackage(appUserModelId);
		return architecture == TargetArchitecture.Unknown ? TargetArchitecture.X64 : architecture;
	}

	private static string NewSessionId() => "session-" + Guid.NewGuid().ToString("N")[..8];

	public async ValueTask DisposeAsync()
	{
		// The poll first, and awaited: it holds a reference to every session and would otherwise be
		// calling into hosts that are being disposed underneath it.
		await _stopping.CancelAsync();

		if (_refreshing is { } refreshing) await refreshing;

		foreach (var session in Sessions)
		{
			await session.DisposeAsync();
		}

		_sessions.Clear();
		_refreshes.Clear();

		_stopping.Dispose();
		_gate.Dispose();
	}
}
