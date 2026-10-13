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

	/// <summary>One worker's row, as <see cref="Workspaces"/> lists it, memory sampled: what a reload answers with.</summary>
	public OperatorWorkspace Workspace(WorkspaceWorker worker) =>
		Row(worker, _options.TimeProvider.GetUtcNow().UtcDateTime, sampleMemory: true);

	/// <summary>
	/// Pulsed as calls start and end and as a session is started, ended or shared, so a wait on <see cref="Sessions"/>
	/// answers at once whether an agent or a person changed it.
	/// </summary>
	public ChangeSignal SessionsChanged { get; } = ChangeSignal.Either(activity.Changed, sessions.Changed);

	/// <summary>
	/// Every debug session, whoever started it, as <see cref="LiveAppSessionManager.Describe"/> lists them, each one a
	/// person shared naming the client it is shared with by that client's row now, and saying whether it is connected.
	/// </summary>
	public OperatorSessionList Sessions()
	{
		var present = clients.ByKey();
		var rows = sessions.Sessions.Select(session => WithParties(session, present)).ToList();

		var stable = rows.Select(row => row with
		{
			Uptime = TimeSpan.Zero,
			LastEventAge = null,
			InfoAge = null,
			Running = Steady(row.Running),
		});

		return new OperatorSessionList { Sessions = rows, Version = ViewVersion.Of(stable) };
	}

	/// <summary>One session's row, as <see cref="Sessions"/> lists it.</summary>
	public LiveAppSessionSummary Session(LiveAppSession session) => WithParties(session, clients.ByKey());

	/// <summary>A session's row, its parties named as <see cref="Named"/> names them.</summary>
	private static LiveAppSessionSummary WithParties(LiveAppSession session, IReadOnlyDictionary<string, ClientSummary> present)
	{
		var summary = session.Describe();

		return Named(summary, session.Owners, session.SetterOf(summary.Stop?.BreakpointId), present);
	}

	/// <summary>
	/// <paramref name="summary"/> naming each agent session in it by the row the client table files that session under
	/// now -- a row goes and is made again with a new id while the session is the same one: the one it is shared with,
	/// saying whether there is one; the one that started it, where the table still lists it; and who set the breakpoint
	/// the target is stopped at, which is <paramref name="setter"/>.
	/// </summary>
	public static LiveAppSessionSummary Named(
		LiveAppSessionSummary summary,
		LiveAppOwners owners,
		LiveAppActor? setter,
		IReadOnlyDictionary<string, ClientSummary> present)
	{
		ArgumentNullException.ThrowIfNull(summary);
		ArgumentNullException.ThrowIfNull(owners);
		ArgumentNullException.ThrowIfNull(present);

		if (owners.Shared is { } shared && summary.SharedWith is { } share)
		{
			var row = present.GetValueOrDefault(shared.Key);

			summary = summary with
			{
				SharedWith = share with
				{
					ClientId = row?.Id ?? share.ClientId,
					Name = row is null ? share.Name : ClientName(row) ?? share.Name,
					Connected = row is not null,
				},
			};
		}

		var stoppedAtBreakpoint = summary.Stop?.BreakpointId is { Length: > 0 };

		return summary with
		{
			StartedByAgent = owners.IsPersons ? null : AgentOf(owners.StarterKey, present),
			StopSetBy = setter is null || !stoppedAtBreakpoint
				? null
				: new LiveBreakpointSetter { Person = setter.IsPerson, Agent = AgentOf(setter.ClientKey, present) },
		};
	}

	/// <summary>The live edits made to <paramref name="session"/>'s app, each agent session named by its row in the client table now.</summary>
	public LiveXamlChangeList XamlChanges(LiveAppSession session) =>
		Changes(session.SessionId, session.XamlChanges, clients.ByKey());

	/// <summary>
	/// The changes <paramref name="log"/> holds, oldest first, each one an agent session made naming that session by
	/// the row the client table files it under now, as <see cref="Named"/> names a session's parties.
	/// </summary>
	public static LiveXamlChangeList Changes(
		string sessionId,
		XamlChangeLog log,
		IReadOnlyDictionary<string, ClientSummary> present)
	{
		ArgumentNullException.ThrowIfNull(log);
		ArgumentNullException.ThrowIfNull(present);

		var (recorded, dropped) = log.Snapshot();
		var rows = recorded
			.Select(entry => entry.Change with { Agent = entry.By.IsPerson ? null : AgentOf(entry.By.ClientKey, present) })
			.ToList();

		return new LiveXamlChangeList
		{
			SessionId = sessionId,
			Changes = rows,
			Dropped = dropped,
			Version = ViewVersion.Of(new { Changes = rows, Dropped = dropped }),
		};
	}

	/// <summary>
	/// The agent session the client table files under <paramref name="clientKey"/>, by its row now; null where the table
	/// lists none, since a row that has gone is no longer one a window can name or send a note to.
	/// </summary>
	private static LiveAppAgent? AgentOf(string? clientKey, IReadOnlyDictionary<string, ClientSummary> present) =>
		clientKey is not null && present.GetValueOrDefault(clientKey) is { } row
			? new LiveAppAgent { ClientId = row.Id, Name = ClientName(row), Connected = true }
			: null;

	/// <summary>
	/// What a window calls a client: what its relay labelled it, else the folder it runs in, else the program; null
	/// where it said none of them.
	/// </summary>
	public static string? ClientName(ClientSummary client) =>
		client.Label
			?? (client.WorkingDirectory is { Length: > 0 } folder ? folder : null)
			?? client.Roots.FirstOrDefault()
			?? client.ClientName;

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
