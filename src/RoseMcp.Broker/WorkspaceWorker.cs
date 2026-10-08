using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Client;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// One worker process and the MCP client talking to it.
/// <para>
/// Worker exit is treated as ordinary rather than exceptional. Workers are expected to die: the
/// solution can be deleted, a hard reload kills one deliberately, and a Roslyn host holding a large
/// solution can run out of memory. The broker's job is to notice, say why, and keep serving.
/// </para>
/// </summary>
public sealed class WorkspaceWorker : IAsyncDisposable
{
	/// <summary>
	/// How the initial load is labelled in the activity log. Not a tool name, because what someone
	/// watching cares about is that the solution is loading, not which call happens to be waiting.
	/// </summary>
	public const string LoadOperation = "load solution";

	/// <summary>
	/// The SDK's own options, not a hand-rolled equivalent. The worker serialises its results with
	/// these, and anything that differs -- string enums being the one that bit -- turns a working
	/// call into a deserialisation failure at the boundary.
	/// </summary>
	private static readonly JsonSerializerOptions SerializerOptions = McpJsonUtilities.DefaultOptions;

	private static readonly Dictionary<string, object?> EmptyArguments = [];

	private readonly McpClient _client;
	private readonly ActivityLog _activities;
	private readonly ILogger _logger;
	private int _refreshingHeap;
	private string? _loadFailure;
	private string? _key;

	// The worker process, held only to be told when it exits. Opened from the pid the worker reports
	// about itself rather than from the transport, which does not expose the child it started.
	private Process? _process;

	/// <summary>Makes the first stop the one recorded, when a crash and a deliberate stop race.</summary>
	private readonly object _stopGate = new();

	/// <summary>
	/// Callers holding this worker for a call, from before the call starts to after it ends. Taken
	/// under the manager's gate, which is what makes "nobody holds it" a fact the sweep can act on
	/// rather than a guess about a call that has been handed the worker and has not started yet.
	/// </summary>
	private int _holds;

	/// <summary><see cref="LastUsedUtc"/> as ticks, so a call ending on one thread and the sweep reading on another cannot tear it.</summary>
	private long _lastUsedTicks;

	private WorkspaceWorker(string solutionPath, McpClient client, ActivityLog activities, ILogger logger)
	{
		SolutionPath = solutionPath;
		_client = client;
		_activities = activities;
		_logger = logger;
		StartedUtc = DateTime.UtcNow;
		_lastUsedTicks = StartedUtc.Ticks;
	}

	public string SolutionPath { get; }

	/// <summary>
	/// Short stable name for this workspace, computed once. Derived from the path, so a caller
	/// holding one from before this worker was replaced can still use it.
	/// </summary>
	public string Key => _key ??= Solutions.WorkspaceKey.For(SolutionPath);

	public DateTime StartedUtc { get; }

	/// <summary>
	/// When a tool call last finished with this worker, or when its first load finished, or when it
	/// started if neither has happened. Only calls routed to it count as use: status, listing and the
	/// tray's polling read it without being use, so a workspace somebody merely watches still goes
	/// idle. The load finishing is not use either; it is when there was first something to use.
	/// </summary>
	public DateTime LastUsedUtc => new(Volatile.Read(ref _lastUsedTicks), DateTimeKind.Utc);

	/// <summary>When it stopped serving, for a worker that has. Null while it runs.</summary>
	public DateTime? StoppedUtc { get; private set; }

	/// <summary>Why it stopped, in words, where the stop had more to say than its exit reason. Null otherwise.</summary>
	public string? StopDetail { get; private set; }

	/// <summary>
	/// When the eviction sweep first saw the solution file missing, or null while it is there. Only
	/// the sweep reads and writes it.
	/// </summary>
	internal DateTime? SolutionMissingSinceUtc { get; private set; }

	/// <summary>
	/// What is wrong with this worker being a different build from the broker, or null where it is
	/// the same one. Held rather than only logged, because the party who needs it is the agent whose
	/// next confusing answer it explains, and a log line reaches nobody mid-session.
	/// </summary>
	public string? VersionMismatch { get; init; }

	/// <summary>
	/// The worker's process id, learned on connect. Held so memory can be sampled from outside
	/// the process, which keeps working when the worker itself has stopped answering.
	/// </summary>
	public int? ProcessId { get; private set; }

	/// <summary>Last managed heap size the worker reported while it was still responding.</summary>
	public long? ManagedHeapBytes { get; private set; }

	/// <summary>
	/// The last status report to pass through, whoever asked for it. The broker asks on connect, so
	/// one arrives the moment the load finishes; every rose_workspace_status a client makes after
	/// that replaces it. Kept because the configuration, the project count and the reasons a
	/// workspace is degraded are otherwise a round trip away, and the tray window, which wants them
	/// every two seconds, has no business making that trip.
	/// </summary>
	public WorkspaceStatusReport? LastStatus { get; private set; }

	/// <summary>How long the initial load took, once it has finished.</summary>
	public TimeSpan? LoadDuration { get; private set; }

	public WorkerExitReason ExitReason { get; private set; } = WorkerExitReason.Running;

	public bool IsAlive => ExitReason == WorkerExitReason.Running;

	/// <summary>
	/// Where the workspace is in its life. The process answers for a dead worker and the last status
	/// report for a live one; a live worker that has not reported yet is loading -- unless its load
	/// already failed, which the broker saw even though no client did.
	/// </summary>
	public WorkspaceState State
	{
		get
		{
			if (!IsAlive)
			{
				return ExitReason == WorkerExitReason.Crashed ? WorkspaceState.Faulted : WorkspaceState.Unloaded;
			}

			if (LastStatus is { } status) return status.State;

			return _loadFailure is null ? WorkspaceState.Loading : WorkspaceState.Faulted;
		}
	}

	/// <summary>
	/// Where a worker stands: an empty folder of Rose's own under the temp directory, made if it is not
	/// there. Anywhere but the solution's directory, because Windows holds a process's working
	/// directory open against deletion, a worktree's solution sits at its root, and a worker stays warm
	/// for the life of the broker -- so a worker standing in its solution's directory makes the worktree
	/// impossible to remove until the broker goes, and the error names "another process" rather than
	/// Rose.
	/// <para>
	/// A folder of its own rather than the temp directory itself, because the current directory is on
	/// the DLL search path and the temp directory is where other programs leave DLLs. Nothing in the
	/// worker reads its working directory: the SDK is chosen, and restore is run, by passing the
	/// solution's directory explicitly, and the solution path arrives absolute.
	/// </para>
	/// </summary>
	private static string WorkerDirectory()
	{
		var directory = Path.Combine(Path.GetTempPath(), "RoseMcpWorker");
		Directory.CreateDirectory(directory);
		return directory;
	}

	public static async Task<WorkspaceWorker> StartAsync(
		string solutionPath,
		string workerPath,
		BrokerOptions options,
		ActivityLog activities,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken,
		WorkspaceBuildOverrides? build = null)
	{
		var logger = loggerFactory.CreateLogger<WorkspaceWorker>();

		// The worker stands somewhere other than the solution's directory (see WorkerDirectory), so a
		// relative path would be resolved against the wrong base. The broker resolves every path before
		// it gets here; this is where that stops being an assumption.
		if (!Path.IsPathFullyQualified(solutionPath))
		{
			throw new ArgumentException(
				$"A worker needs an absolute solution path, and was given '{solutionPath}'.", nameof(solutionPath));
		}

		var arguments = new List<string> { "--solution", solutionPath };
		if (options.NoRestore) arguments.Add("--no-restore");

		// MSBuild properties are per process, so this is the only place they can be applied: a
		// worker cannot change the configuration it loaded under without being restarted.
		if (build?.Configuration is { Length: > 0 } configuration)
		{
			arguments.Add("--configuration");
			arguments.Add(configuration);
		}

		if (build?.Platform is { Length: > 0 } platform)
		{
			arguments.Add("--platform");
			arguments.Add(platform);
		}

		foreach (var property in build?.Properties ?? [])
		{
			arguments.Add("--property");
			arguments.Add(property);
		}

		var transport = new StdioClientTransport(
			new StdioClientTransportOptions
			{
				Command = workerPath,
				Arguments = arguments,

				// Task Manager's Details tab shows this, which is how a human works out which of
				// several identical worker processes belongs to which solution.
				Name = $"rose-worker {Path.GetFileNameWithoutExtension(solutionPath)}",
				WorkingDirectory = WorkerDirectory(),
			},
			loggerFactory);

		logger.LogInformation("Starting a worker for {SolutionPath}.", solutionPath);

		// The handshake budget is set rather than inherited: the SDK defaults to 60 seconds, which a
		// cold worker loses to its own design-time build when several start at once.
		var client = await McpClient.CreateAsync(
			transport,
			ChildHostHandshake.Options(options.WorkerHandshakeTimeout),
			loggerFactory,
			cancellationToken);

		var worker = new WorkspaceWorker(solutionPath, client, activities, logger)
		{
			VersionMismatch = ChildHostVersion.Mismatch(
				client.ServerInfo?.Version, workerPath, typeof(WorkspaceWorker).Assembly),
		};

		if (worker.VersionMismatch is not null) logger.LogWarning("{Mismatch}", worker.VersionMismatch);

		await worker.RefreshProcessInfoAsync(cancellationToken);
		worker.BeginLoading();

		return worker;
	}

	/// <summary>
	/// Forwards a tool call, records it as an activity, and deserialises the worker's structured
	/// result.
	/// <para>
	/// A worker's tool takes the broker's arguments minus the workspace one, so routing is a straight
	/// pass-through: the name and the argument dictionary go over as they arrived. Nothing checks that
	/// the two schemas agree, and their parameter text has drifted.
	/// </para>
	/// <para>
	/// A progress sink is the calling client's, when it asked for one; progress reaches the
	/// activity log either way, so a long call shows up in the tray even when nobody else is
	/// watching. An operation overrides the activity's label, which is otherwise the tool name.
	/// </para>
	/// </summary>
	public async Task<T> CallAsync<T>(
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null,
		string? operation = null)
	{
		// One line per forwarded call, so which rose_* tools a session actually reaches for is
		// measurable from the files already being written rather than from anybody's recollection. The
		// origin directory is the closest thing to a session identity a worker call has -- it is a
		// working directory in practice -- and it is null for a client with no relay in front of it.
		_logger.LogInformation(
			"Forwarding {Tool} to {WorkspaceKey} for {Origin}.",
			tool,
			Key,
			CallOrigin.Directory ?? "(no origin)");

		using var activity = _activities.Begin(SolutionPath, operation ?? tool, DescribeTarget(arguments), progress);

		try
		{
			var result = await SendAsync<T>(tool, arguments, activity, cancellationToken);

			// Whoever asked, the answer describes this worker, and it is the freshest one there is.
			if (result is WorkspaceStatusReport status) LastStatus = status;

			RefreshHeapSoon();

			return result;
		}
		catch (OperationCanceledException)
		{
			activity.Complete(ActivityOutcome.Cancelled);
			throw;
		}
		catch (Exception exception)
		{
			activity.Complete(ActivityOutcome.Failed, exception.Message);
			throw;
		}
	}

	/// <summary>
	/// Records that the worker has stopped serving, and when, the first time only: the first reason
	/// is the true one. A worker the broker stops on purpose then fails its in-flight calls and exits
	/// the same way a crash does, and neither may relabel it. <paramref name="detail"/> is why, in
	/// words, for a stop that has more to say than its reason -- an eviction says how long the worker
	/// sat unused.
	/// </summary>
	public void MarkStopped(WorkerExitReason reason, string? detail = null)
	{
		lock (_stopGate)
		{
			if (!IsAlive) return;

			StoppedUtc = DateTime.UtcNow;
			StopDetail = detail;
			ExitReason = reason;
		}
	}

	/// <summary>
	/// Holds this worker for one call until the returned handle is disposed, so the eviction sweep
	/// leaves it alone. Taken only under the manager's gate, which the sweep also holds while it
	/// decides, so a worker handed to a caller is never stopped between being handed over and being
	/// called. <paramref name="use"/> says whether the call counts as use, which restarts the idle
	/// clock now and again when the call ends: a call that runs for an hour has not left the worker
	/// idle for that hour.
	/// </summary>
	internal IDisposable Hold(bool use)
	{
		Interlocked.Increment(ref _holds);
		if (use) Touch();

		return new WorkerHold(this, use);
	}

	/// <summary>
	/// Records whether the solution file is there, for the sweep's grace period. Missing counts from
	/// the first sweep that saw it gone, and any sweep that sees it back starts the count over.
	/// </summary>
	internal void ObserveSolution(bool exists, DateTime nowUtc) =>
		SolutionMissingSinceUtc = exists ? null : SolutionMissingSinceUtc ?? nowUtc;

	/// <summary>What the eviction sweep decides on, read now.</summary>
	public EvictionFacts EvictionFacts() => new(
		Alive: IsAlive,
		Loading: State == WorkspaceState.Loading,
		Busy: Volatile.Read(ref _holds) > 0 || _activities.Running(SolutionPath).Count > 0,
		LastUsedUtc: LastUsedUtc,
		StoppedUtc: StoppedUtc,
		SolutionMissingSinceUtc: SolutionMissingSinceUtc);

	/// <summary>
	/// This worker as <c>rose_workspace_list</c> reports it. Broker-side facts only, so listing never
	/// waits on the worker and never counts as using it.
	/// </summary>
	public WorkspaceListEntry ListEntry(DateTime nowUtc)
	{
		var idle = nowUtc - LastUsedUtc;

		return new WorkspaceListEntry
		{
			Workspace = SolutionPath,
			WorkspaceKey = Key,
			State = State,
			ExitReason = IsAlive ? null : ExitReason.ToString(),
			ProjectCount = LastStatus?.Projects.Count,
			IdleFor = idle < TimeSpan.Zero ? TimeSpan.Zero : idle,
			Running = _activities.Running(SolutionPath).Count,
		};
	}

	/// <summary>
	/// Status for a worker that has stopped, answered from what the broker knows rather than by
	/// starting a fresh one. Asking whether a workspace is healthy is looking at it; starting a worker
	/// to answer would load the solution again and wipe the record of why it stopped, so a session
	/// that checks status now and then would keep an evicted workspace warm and never learn it had
	/// been evicted.
	/// <para>
	/// The shape the worker itself gives once its solution is gone: nothing is loaded, so no project
	/// is, and revision 0 identifies no snapshot. Why it stopped is the degraded reason, because it is
	/// the reason there are no answers, and the notice says what brings it back.
	/// </para>
	/// </summary>
	internal WorkspaceStatusReport StoppedStatus() => new()
	{
		SolutionPath = SolutionPath,
		State = State,
		Revision = 0,
		Projects = [],
		LoadDiagnostics = [],
		DegradedReasons = [StopDescription()],
		Notices =
		[
			"Nothing is loaded, and asking for status started nothing. The next call that needs this "
				+ "workspace starts a fresh worker; rose_workspace_reload starts one now.",
		],
	};

	/// <summary>Why this worker stopped, as one sentence for a person.</summary>
	private string StopDescription() => ExitReason switch
	{
		WorkerExitReason.Evicted => $"The worker was evicted. {StopDetail}".TrimEnd(),
		WorkerExitReason.Crashed => "The worker exited on its own. Its log says why.",
		WorkerExitReason.SolutionUnloaded => "The worker unloaded the solution when its file went away.",
		_ => "The worker was stopped.",
	};

	private void Touch() => Volatile.Write(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

	/// <summary>
	/// One caller's hold on the worker. Released once however often it is disposed, so a caller that
	/// disposes on two paths cannot release somebody else's hold.
	/// </summary>
	private sealed class WorkerHold(WorkspaceWorker worker, bool use) : IDisposable
	{
		private int _released;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _released, 1) != 0) return;

			// The idle clock restarts before the hold goes, so a sweep that sees nobody holding the
			// worker also sees the call that just finished.
			if (use) worker.Touch();

			Interlocked.Decrement(ref worker._holds);
		}
	}

	/// <summary>
	/// Asks the worker who it is. Cheap by design -- it loads nothing -- so it is safe to call on
	/// connect before the solution has been opened. Deliberately untracked: bookkeeping calls in
	/// the activity list would bury the ones a human is actually looking for.
	/// </summary>
	public async Task RefreshProcessInfoAsync(CancellationToken cancellationToken)
	{
		try
		{
			var info = await SendAsync<WorkerInfo>(ToolNames.WorkerInfo, EmptyArguments, progress: null, cancellationToken);
			ProcessId = info.ProcessId;
			ManagedHeapBytes = info.ManagedHeapBytes;

			WatchForExit(info.ProcessId);
		}
		catch (Exception exception)
		{
			// Memory reporting is a nicety. Losing it must not stop the workspace from opening.
			_logger.LogDebug(exception, "Could not read worker info for {SolutionPath}.", SolutionPath);
		}
	}

	/// <summary>
	/// Subscribes to the worker process exiting, so a crash is noticed when it happens rather than
	/// on the next call.
	/// <para>
	/// Nothing observed exit before: <see cref="ExitReason"/> flipped inside <c>SendAsync</c>, so a
	/// worker that crashed while idle went on describing itself as alive until somebody called it.
	/// The tray polls <see cref="Describe"/> every couple of seconds and showed a crashed worker as
	/// loaded for as long as nobody asked it anything, which is exactly the situation a person is
	/// looking at that window in.
	/// </para>
	/// <para>
	/// Only when this side still thinks it is alive, so a worker the broker closed on purpose keeps
	/// <see cref="WorkerExitReason.StoppedByBroker"/> rather than being relabelled a crash by its own
	/// orderly exit.
	/// </para>
	/// </summary>
	private void WatchForExit(int processId)
	{
		if (_process is not null) return;

		try
		{
			var process = Process.GetProcessById(processId);
			process.EnableRaisingEvents = true;
			process.Exited += (_, _) =>
			{
				if (!IsAlive) return;

				MarkStopped(WorkerExitReason.Crashed);
				_logger.LogWarning("The worker for {SolutionPath} exited on its own.", SolutionPath);
			};

			_process = process;

			// Between opening the handle and arming the event the process can already have gone, and
			// Exited does not fire for an exit that happened first.
			if (process.HasExited) MarkStopped(WorkerExitReason.Crashed);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
		{
			// No such process, or it went between being reported and being looked up. The call path
			// still notices, which is what this improves on rather than replaces.
			_logger.LogDebug(exception, "Could not watch worker {ProcessId} for exit.", processId);
		}
	}

	/// <summary>
	/// Re-reads the managed heap once the work that changes it has finished. It is first read on
	/// connect, before anything has loaded, and without this the window would show that first
	/// reading -- a few dozen megabytes -- for the life of the process. Coalesced, so a burst of
	/// calls costs one round trip, and skipped for a worker on its way out.
	/// </summary>
	private void RefreshHeapSoon()
	{
		if (!IsAlive || Interlocked.CompareExchange(ref _refreshingHeap, 1, 0) != 0) return;

		_ = Task.Run(async () =>
		{
			try
			{
				await RefreshProcessInfoAsync(CancellationToken.None);
			}
			finally
			{
				Volatile.Write(ref _refreshingHeap, 0);
			}
		});
	}

	/// <summary>
	/// Samples memory from the process table rather than asking the worker, so the numbers stay
	/// truthful for a worker that is wedged -- which is exactly when someone is looking at them.
	/// </summary>
	public WorkspaceSummary Describe()
	{
		long? workingSet = null;
		long? privateMemory = null;

		if (ProcessId is { } id)
		{
			try
			{
				using var process = Process.GetProcessById(id);
				workingSet = process.WorkingSet64;
				privateMemory = process.PrivateMemorySize64;
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
			{
				// The process is gone. Reporting no numbers is more honest than reporting stale ones.
			}
		}

		// Read once: a client's status call can replace it between one property and the next.
		var status = LastStatus;

		return new WorkspaceSummary
		{
			Workspace = SolutionPath,
			WorkspaceKey = Key,
			DisplayName = Path.GetFileNameWithoutExtension(SolutionPath),
			Alive = IsAlive,
			ExitReason = ExitReason.ToString(),
			State = State,
			StartedUtc = StartedUtc,
			Uptime = DateTime.UtcNow - StartedUtc,
			ProcessId = ProcessId,
			WorkingSetBytes = workingSet,
			PrivateMemoryBytes = privateMemory,
			ManagedHeapBytes = ManagedHeapBytes,
			BuildConfiguration = status?.BuildConfiguration,
			ProjectCount = status?.Projects.Count,
			FailedProjects = status is null
				? []
				: [.. status.Projects.Where(project => !project.LoadedSuccessfully).Select(project => project.Name)],
			DegradedReasons = status?.DegradedReasons ?? (_loadFailure is null ? [] : [_loadFailure]),
			Notices = VersionMismatch is null
				? status?.Notices ?? []
				: [.. status?.Notices ?? [], VersionMismatch],
			LoadSeconds = LoadDuration?.TotalSeconds,
			Running = _activities.Running(SolutionPath),
			Recent = _activities.Recent(SolutionPath),
		};
	}

	/// <summary>
	/// Asks for status straight away, purely so the load has something to report progress against.
	/// <para>
	/// A worker starts loading the moment it launches, whether or not anyone has called it, and
	/// progress notifications only exist in the context of a request. With no call in flight the
	/// first half-minute of a large solution is invisible -- which is exactly what a tray reload
	/// produces, since no client is waiting on it. The work is not wasted: this is the same
	/// design-time build and generator pass the first real call would have paid for.
	/// </para>
	/// </summary>
	private void BeginLoading() => _ = FollowLoadAsync();

	private async Task FollowLoadAsync()
	{
		var load = Stopwatch.StartNew();

		try
		{
			await CallAsync<WorkspaceStatusReport>(
				ToolNames.WorkspaceStatus,
				EmptyArguments,
				CancellationToken.None,
				operation: LoadOperation);

			// The idle clock starts when the worker became usable, not when its process did. Counting
			// the load as idle would evict a solution that loads slowly soon after it is ready, and
			// one that loads for longer than the idle limit the moment it finishes. Before the
			// duration, so whoever sees the load finished also sees the clock restarted.
			Touch();
			LoadDuration = load.Elapsed;
		}
		catch (Exception exception)
		{
			// Nothing is waiting on this result. A load failure is reported to whoever calls next,
			// and the activity already records that it failed -- but it is remembered here too, so
			// the tray can say so about a worker no client has spoken to yet.
			_loadFailure = $"Loading the solution failed: {exception.Message}";
			_logger.LogDebug(exception, "Following the load of {SolutionPath} ended early.", SolutionPath);
		}
	}

	private async Task<T> SendAsync<T>(
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		IProgress<ProgressNotificationValue>? progress,
		CancellationToken cancellationToken)
	{
		ModelContextProtocol.Protocol.CallToolResult result;
		try
		{
			// Not CallToolAsync: it abandons the wait without telling the worker, which then finishes
			// the whole operation. Reads on a workspace are ordered, so that abandoned work is the
			// delay before the next call on it can start.
			result = await CancellableToolCall.InvokeAsync(_client, tool, arguments, progress, cancellationToken);
		}
		catch (Exception exception) when (IsTransportFailure(exception))
		{
			// The real call is the honest liveness test. Pinging first would add a round trip to
			// every request and still answer for a moment that has already passed.
			//
			// Unless the worker was already stopped on purpose: a call in flight when the broker
			// closes a worker fails the same way, and calling that a crash would be a lie.
			if (IsAlive)
			{
				MarkStopped(WorkerExitReason.Crashed);
				_logger.LogWarning(exception, "The worker for {SolutionPath} died during {Tool}.", SolutionPath, tool);
			}

			throw new WorkerUnavailableException(SolutionPath, exception);
		}

		if (result.IsError == true)
		{
			throw new InvalidOperationException(
				ForwardedError.Message(result) ?? $"The worker for {SolutionPath} reported an error running {tool}.");
		}

		if (result.StructuredContent is null)
		{
			throw new InvalidOperationException($"The worker returned no structured content for {tool}.");
		}

		return result.StructuredContent.Value.Deserialize<T>(SerializerOptions)
			?? throw new InvalidOperationException($"Could not read the worker's {tool} result.");
	}

	/// <summary>
	/// What the call is aimed at, for the activity row. Arguments are all the broker knows about a
	/// call, and "rose_rename_symbol" on its own answers none of the questions someone watching a
	/// queue of them would ask.
	/// </summary>
	private static string? DescribeTarget(IReadOnlyDictionary<string, object?> arguments)
	{
		if (Text(arguments, "filePath") is { } filePath)
		{
			var name = Path.GetFileName(filePath);
			var position = Text(arguments, "line") is { } line ? $"{name}:{line}" : name;

			return Text(arguments, "newName") is { } newName ? $"{position} to {newName}" : position;
		}

		return Text(arguments, "target")
			?? Text(arguments, "hintName")
			?? Text(arguments, "query")
			?? Text(arguments, "project")
			?? Text(arguments, "scope");
	}

	private static string? Text(IReadOnlyDictionary<string, object?> arguments, string key) =>
		arguments.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

	/// <summary>
	/// Whether a failure means the worker is gone rather than the request being bad. A tool that
	/// throws is a normal error result; a transport that closes is a dead process.
	/// </summary>
	private static bool IsTransportFailure(Exception exception) => exception
		is ClientTransportClosedException
		or IOException
		or ObjectDisposedException
		or InvalidOperationException { Source: "ModelContextProtocol.Core" };

	public async ValueTask DisposeAsync()
	{
		MarkStopped(WorkerExitReason.StoppedByBroker);

		try
		{
			// Disposing the client closes the worker's stdin, which is what tells it to exit. That
			// is the same mechanism that stops workers outliving a broker that dies.
			await _client.DisposeAsync();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "The worker for {SolutionPath} did not shut down cleanly.", SolutionPath);
		}

		// The exit watch goes with the worker it was watching. Held open it is one handle per worker
		// ever started, in a broker that replaces them routinely.
		_process?.Dispose();
		_process = null;
	}
}
