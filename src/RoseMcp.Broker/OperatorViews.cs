using Microsoft.Extensions.Options;

using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Broker;

/// <summary>
/// The views a person's windows read through the operator API, each made from the broker's own objects and given
/// the version a long poll waits past: who is connected, which workspaces are loaded, what was done, which debug
/// sessions there are, and what all of it holds in memory.
/// <para>
/// Broker-side reads only. Nothing here calls a worker or a live-app host, waits on a load, or counts as using a
/// workspace, so a window watching every view keeps nothing warm and is answered while a solution loads; and none
/// of it is a tool call, so the activity record does not hold a window's reads.
/// </para>
/// </summary>
public sealed class OperatorViews(
	ConnectedClients clients,
	WorkspaceManager workspaces,
	LiveAppSessionManager sessions,
	ActivityRecord activity,
	IOptions<BrokerOptions> options)
{
	private readonly BrokerOptions _options = options.Value;

	/// <summary>Pulsed by the client table as calls arrive, so a wait on <see cref="Clients"/> answers at once.</summary>
	public ChangeSignal ClientsChanged => clients.Changed;

	/// <summary>Pulsed by the activity record as calls start and end, so a wait on <see cref="Activity"/> answers at once.</summary>
	public ChangeSignal ActivityChanged => activity.Changed;

	/// <summary>Every client the broker keeps, as <see cref="ConnectedClients.Describe"/> lists them.</summary>
	public ClientList Clients()
	{
		var rows = clients.Describe();

		return new ClientList { Clients = rows, Version = ViewVersion.Of(rows) };
	}

	/// <summary>
	/// Every workspace the broker holds a worker for, running or stopped, ordered by path.
	/// <paramref name="sampleMemory"/> false leaves the memory out, for a wait that only compares versions, which
	/// memory is no part of.
	/// </summary>
	public OperatorWorkspaceList Workspaces(bool sampleMemory = true)
	{
		var now = _options.TimeProvider.GetUtcNow().UtcDateTime;

		var rows = workspaces.Workers
			.OrderBy(worker => worker.SolutionPath, PathCasing.Comparer)
			.Select(worker => Row(worker, now, sampleMemory))
			.ToList();

		var stable = rows.Select(row => row with
		{
			IdleFor = TimeSpan.Zero,
			Summary = row.Summary with
			{
				Uptime = TimeSpan.Zero,
				WorkingSetBytes = null,
				PrivateMemoryBytes = null,
				ManagedHeapBytes = null,
				Running = Steady(row.Summary.Running),
			},
		});

		return new OperatorWorkspaceList
		{
			Workspaces = rows,
			IdleEvictionAfter = _options.IdleEvictionAfter,
			Version = ViewVersion.Of(new { Workspaces = stable, _options.IdleEvictionAfter }),
		};
	}

	/// <summary>Every debug session, whoever started it, as <see cref="LiveAppSessionManager.Describe"/> lists them.</summary>
	public OperatorSessionList Sessions()
	{
		var rows = sessions.Describe();

		var stable = rows.Select(row => row with
		{
			Uptime = TimeSpan.Zero,
			LastEventAge = null,
			InfoAge = null,
			Running = Steady(row.Running),
		});

		return new OperatorSessionList { Sessions = rows, Version = ViewVersion.Of(stable) };
	}

	/// <summary>
	/// The page of the activity record <paramref name="query"/> asks for. Its version is worked out without the
	/// bodies, so a page read with them and one read without carry the same version, and a wait can compare the
	/// cheaper read.
	/// </summary>
	/// <exception cref="ArgumentException">The query's cursor is not one a page gave.</exception>
	public ActivityPage Activity(ActivityQuery query)
	{
		var page = activity.Query(query);

		var stable = page with
		{
			Bodies = false,
			Entries = [.. page.Entries.Select(entry => entry with
			{
				Arguments = null,
				Response = null,
				Error = null,
				ErrorFullLength = null,
				Changes = [.. entry.Changes.Select(change => change with { Diff = null })],
			})],
		};

		return page with { Version = ViewVersion.Of(stable) };
	}

	/// <summary>
	/// <paramref name="query"/> asking for the same entries without their bodies: what a wait reads every second,
	/// rather than read each body back from disk to compare versions that leave them out.
	/// </summary>
	public static ActivityQuery WithoutBodies(ActivityQuery query) =>
		query.Bodies ? query with { Bodies = false, Limit = Math.Min(query.Limit, ActivityQuery.MaxLimitWithBodies) } : query;

	/// <summary>
	/// What the broker, each running worker and each debug session's host and app hold now, and the machine's
	/// memory. Sampled on every call, opening each process once.
	/// </summary>
	public MemoryReport Memory()
	{
		var workers = workspaces.Workers
			.Where(worker => worker.IsAlive)
			.OrderBy(worker => worker.SolutionPath, PathCasing.Comparer)
			.Select(worker => new WorkerMemory
			{
				Workspace = worker.SolutionPath,
				WorkspaceKey = worker.Key,
				Process = worker.Memory(),
			});

		return new MemoryReport
		{
			SampledUtc = _options.TimeProvider.GetUtcNow().UtcDateTime,
			Broker = ProcessMemorySampler.Current(),
			Workers = [.. workers],
			LiveApps = [.. sessions.Describe().Select(LiveApp)],
			Machine = MachineMemoryProbe.Read(),
		};
	}

	/// <summary>
	/// One session's host and app. The host is read while the session has not ended, and only where it started
	/// before the session did, which the host the session connected to always has; the app only while the host
	/// reports it attached and serving, since the host says the session has ended within a poll of the app
	/// exiting. Either way an exited process's id, which Windows gives to the next process it starts, is not read.
	/// </summary>
	private static LiveAppMemory LiveApp(LiveAppSessionSummary session)
	{
		var hostRuns = session.State != LiveAppSessionState.Ended && session.HostProcessId is not null;
		var targetRuns = session.State == LiveAppSessionState.Ready && session.TargetProcessId is not null;

		return new LiveAppMemory
		{
			SessionId = session.SessionId,
			TargetDescription = session.TargetDescription,
			Host = hostRuns ? ProcessMemorySampler.Read(session.HostProcessId!.Value, startedByUtc: session.StartedUtc) : null,
			Target = targetRuns ? ProcessMemorySampler.Read(session.TargetProcessId!.Value) : null,
		};
	}

	/// <summary>Operations in flight without how long each has taken so far, which differs on every read.</summary>
	private static IReadOnlyList<WorkerActivity> Steady(IReadOnlyList<WorkerActivity> running) =>
		[.. running.Select(operation => operation with { Elapsed = TimeSpan.Zero })];

	private OperatorWorkspace Row(WorkspaceWorker worker, DateTime now, bool sampleMemory)
	{
		var summary = workspaces.Describe(worker, sampleMemory);
		var facts = worker.EvictionFacts();
		var (configuration, platform) = Loaded(summary.BuildConfiguration, worker.Build);
		var idle = now - facts.LastUsedUtc;

		return new OperatorWorkspace
		{
			Summary = summary,
			Folder = Path.GetDirectoryName(worker.SolutionPath) ?? worker.SolutionPath,
			Configuration = configuration,
			Platform = platform,
			BuildProperties = worker.Build?.Properties ?? [],
			LastUsedUtc = facts.LastUsedUtc,
			IdleFor = idle < TimeSpan.Zero ? TimeSpan.Zero : idle,
			InUse = facts.Alive && (facts.Busy || facts.Loading),
			ClosesAtUtc = _options.IdleEvictionAfter is { } idleAfter
				? WorkerEviction.ClosesAt(facts, idleAfter, _options.SolutionGoneGrace)
				: null,
			RebuiltAnalyzers = worker.IsAlive ? worker.RebuiltAnalyzers : [],
		};
	}

	/// <summary>
	/// The configuration and platform a solution was loaded under. The worker's status says it as
	/// <c>Configuration|Platform</c>, with any further properties after it in brackets; until the first status
	/// arrives, what the worker was started with is the best answer, and nothing where it chose its own.
	/// </summary>
	public static (string? Configuration, string? Platform) Loaded(string? reported, WorkspaceBuildOverrides? started)
	{
		if (string.IsNullOrWhiteSpace(reported)) return (started?.Configuration, started?.Platform);

		var bracket = reported.IndexOf(" (", StringComparison.Ordinal);
		var head = bracket < 0 ? reported : reported[..bracket];
		var bar = head.IndexOf('|', StringComparison.Ordinal);

		return bar < 0 ? (head.Trim(), started?.Platform) : (head[..bar].Trim(), head[(bar + 1)..].Trim());
	}
}
