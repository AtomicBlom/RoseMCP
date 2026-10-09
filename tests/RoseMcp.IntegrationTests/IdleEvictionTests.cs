using System.Diagnostics;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The eviction sweep against real workers: an idle one is stopped and its process goes, the
/// reason stays readable where a person looks, the next call brings the workspace back by itself,
/// and a worker whose solution has been removed is retired without waiting to go idle.
/// <para>
/// Which worker the sweep picks is the unit suite's business; what only a real worker can show is
/// that stopping one frees the process, that a later call is served rather than refused, and that
/// <c>rose_workspace_list</c> says the same thing as the tray's row throughout.
/// </para>
/// </summary>
public sealed class IdleEvictionTests
{
	/// <summary>Fast enough to finish in seconds, slow enough that a load is never mistaken for idleness.</summary>
	private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(250);

	private static readonly Dictionary<string, object?> NoArguments = [];

	[Test]
	public async Task An_idle_worker_is_evicted_said_so_and_reopened_by_the_next_call()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		// Idle is decided on a clock the test moves. On the real clock a short limit is a race with the
		// machine: a loaded test process stalls for longer than the limit between the load finishing and
		// the warm half being asserted, and the worker is evicted -- correctly -- before anyone looks.
		// The limit is also how long the stopped row stays, which the clock holds still until the test
		// is done with it.
		var clock = new SteerableClock();
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromMinutes(30);
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		await manager.CallAsync<WorkspaceStatusReport>(
			hints, ToolNames.WorkspaceStatus, NoArguments, retryIfWorkerDied: true, cancellationToken);

		var worker = manager.Workers.ShouldHaveSingleItem();
		var processId = worker.ProcessId.ShouldNotBeNull();

		// Status is a look, not a use: it must not restart the idle clock. Read after the priming
		// load has filed its finish, which restarts the clock itself and can land just after the call.
		await WaitUntilAsync(() => worker.LoadDuration is not null, TimeSpan.FromSeconds(30), cancellationToken);
		var usedBefore = worker.LastUsedUtc;
		await manager.StatusAsync(hints, cancellationToken);
		worker.LastUsedUtc.ShouldBe(usedBefore);

		var warm = manager.List().Workspaces.ShouldHaveSingleItem();
		warm.WorkspaceKey.ShouldBe(worker.Key);
		warm.ExitReason.ShouldBeNull();
		warm.State.ShouldBe(WorkspaceState.Loaded);
		manager.List().IdleEvictionAfter.ShouldBe(TimeSpan.FromMinutes(30));

		clock.Jump(TimeSpan.FromHours(1));
		await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(60), cancellationToken);

		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);
		EvictionNote(manager).ShouldNotBeNull().Message.ShouldNotBeNull().ShouldContain("Unused for");

		// Listed, and described to the tray, as evicted, with the reason in its history.
		var evicted = manager.List().Workspaces.ShouldHaveSingleItem();
		evicted.ExitReason.ShouldBe(nameof(WorkerExitReason.Evicted));
		evicted.State.ShouldBe(WorkspaceState.Unloaded);

		var stoppedRow = manager.Describe().ShouldHaveSingleItem();
		stoppedRow.Alive.ShouldBeFalse();

		// The process is gone and its id free for another to take, so the row reports no memory
		// rather than sampling whatever holds that id now.
		stoppedRow.WorkingSetBytes.ShouldBeNull();
		stoppedRow.PrivateMemoryBytes.ShouldBeNull();
		stoppedRow.ManagedHeapBytes.ShouldBeNull();

		// Status is answered from the stopped row: it says why, starts nothing, and leaves the
		// reason where the tray reads it.
		var stoppedStatus = await manager.StatusAsync(hints, cancellationToken);

		stoppedStatus.State.ShouldBe(WorkspaceState.Unloaded);
		stoppedStatus.Revision.ShouldBe(0);
		stoppedStatus.WorkspaceKey.ShouldBe(worker.Key);
		stoppedStatus.DegradedReasons.ShouldHaveSingleItem().ShouldContain("evicted");
		stoppedStatus.Notices.ShouldContain(notice => notice.Contains("started nothing", StringComparison.Ordinal));
		manager.Workers.ShouldHaveSingleItem().ShouldBeSameAs(worker);
		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);
		EvictionNote(manager).ShouldNotBeNull();

		// A routing failure does not offer the stopped row as somewhere already open.
		var unrouted = Should.Throw<McpException>(() => manager.WorkspaceFor(WorkspaceHints.None));
		unrouted.Message.ShouldNotContain("Already open");

		await WaitUntilAsync(() => ProcessHasExited(processId), TimeSpan.FromSeconds(30), cancellationToken);

		// The next call is served, by a fresh worker, without anyone reopening anything.
		var status = await manager.CallAsync<WorkspaceStatusReport>(
			hints, ToolNames.WorkspaceStatus, NoArguments, retryIfWorkerDied: false, cancellationToken);

		status.WorkspaceKey.ShouldBe(worker.Key);

		var replacement = manager.Workers.ShouldHaveSingleItem();
		replacement.ShouldNotBeSameAs(worker);
		replacement.IsAlive.ShouldBeTrue();
		manager.List().Workspaces.ShouldHaveSingleItem().ExitReason.ShouldBeNull();
	}

	/// <summary>
	/// A removed worktree takes its solution file with it while the worker runs on, holding memory
	/// nobody can use. It is retired once the file has been gone past the grace period, well before
	/// it would have gone idle, and its row is dropped once it has been stopped as long as the idle
	/// limit -- so removed worktrees do not leave a row each behind them either.
	/// </summary>
	[Test]
	public async Task A_worker_whose_solution_is_gone_is_retired_then_forgotten()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		// On a clock the test moves, so the retirement under test is the solution going and never the
		// idle limit passing during a stall, and the stopped row stays until the test moves past the limit.
		var clock = new SteerableClock();
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromMinutes(30);
			options.SolutionGoneGrace = TimeSpan.FromMilliseconds(500);
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		await manager.CallAsync<WorkspaceStatusReport>(
			hints, ToolNames.WorkspaceStatus, NoArguments, retryIfWorkerDied: true, cancellationToken);

		var worker = manager.Workers.ShouldHaveSingleItem();

		// After the load: a loading worker is never evicted, however long its solution has been gone, and
		// on a loaded machine the load outlasts a short wait. The wait is long for the same reason -- a
		// test process here stalls for ten seconds and more at a time.
		await WaitUntilAsync(() => worker.LoadDuration is not null, TimeSpan.FromSeconds(60), cancellationToken);

		File.Delete(fixture.SolutionPath);

		await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(60), cancellationToken);

		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);
		EvictionNote(manager).ShouldNotBeNull().Message.ShouldNotBeNull().ShouldContain("missing");

		// The key sent under the wrong argument is named as this workspace's, without the promise that a
		// call loads it again: the solution file it would load from is gone.
		var misread = WorkspaceHints.From(RootedPath.Absolute(Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, worker.Key)));
		var refused = Should.Throw<McpException>(() => manager.WorkspaceFor(misread));
		refused.Message.ShouldContain($"It is the key of {worker.SolutionPath}");
		refused.Message.ShouldContain("solution file is gone, so it cannot be loaded again");
		refused.Message.ShouldNotContain("starts a fresh one");

		clock.Jump(TimeSpan.FromHours(1));
		await WaitUntilAsync(() => manager.Workers.Count == 0, TimeSpan.FromSeconds(60), cancellationToken);

		manager.List().Workspaces.ShouldBeEmpty();
	}

	/// <summary>
	/// The idle clock starts when the load finishes, and a held worker is never evicted however long
	/// it sits idle -- the hold is what keeps a worker handed to a call alive until the call starts.
	/// Once the hold goes, the same worker is evicted on the next sweeps.
	/// </summary>
	[Test]
	public async Task A_held_worker_outlives_the_idle_limit_and_is_evicted_once_released()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var idleAfter = TimeSpan.FromSeconds(2);
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = idleAfter;
			options.EvictionSweepInterval = SweepInterval;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		// Held from the start, not used: the load alone decides the idle clock here.
		var (worker, hold) = await manager.HoldAsync(hints, use: false, cancellationToken);

		using (hold)
		{
			await WaitUntilAsync(() => worker.LoadDuration is not null, TimeSpan.FromMinutes(2), cancellationToken);

			// Not earlier than the end of the load, allowing for the two clocks' resolution.
			var loadEnded = worker.StartedUtc + worker.LoadDuration!.Value;
			worker.LastUsedUtc.ShouldBeGreaterThanOrEqualTo(loadEnded - TimeSpan.FromMilliseconds(100));

			worker.EvictionFacts().Busy.ShouldBeTrue();

			await Task.Delay(idleAfter + SweepInterval * 8, cancellationToken);

			worker.IsAlive.ShouldBeTrue("a held worker must outlive the idle limit");
			EvictionNote(manager).ShouldBeNull();
		}

		worker.EvictionFacts().Busy.ShouldBeFalse();

		await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(30), cancellationToken);

		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);
	}

	/// <summary>
	/// A change reaching a sibling solution whose worker was evicted does not call that sibling
	/// open: it holds nothing, so nothing there has the new text loaded.
	/// </summary>
	[Test]
	public async Task A_sibling_whose_worker_was_evicted_is_not_called_open()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Siblings", "Repo.slnx");

		// Idle is decided on a clock the test moves, so the sibling is evicted only when the test says,
		// however long the held solution takes to load. The limit is also how long the stopped row
		// stays, which has to outlast stopping the process and the rename that follows; the clock holds
		// still until the test is done with it.
		var clock = new SteerableClock();
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromMinutes(30);
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
		});

		var installer = fixture.Path("Siblings", "Repo.Installer.slnx");

		// The solution being edited is held, so only the sibling goes idle.
		var (_, hold) = await manager.HoldAsync(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), use: false, cancellationToken);

		using (hold)
		{
			await manager.CallAsync<WorkspaceStatusReport>(
				WorkspaceHints.From(RootedPath.Absolute(installer)),
				ToolNames.WorkspaceStatus,
				NoArguments,
				retryIfWorkerDied: true,
				cancellationToken);

			// After both loads have filed their finish, which restarts the idle clock and would undo
			// a jump made before it.
			await WaitUntilAsync(
				() => manager.Workers.All(worker => worker.LoadDuration is not null), TimeSpan.FromMinutes(2), cancellationToken);
			clock.Jump(TimeSpan.FromHours(1));
			await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(60), cancellationToken);

			var tools = new RoseMcp.Broker.Tools.BrokerAnalysisTools(manager, CreatePaths());
			var renamed = await tools.RenameSymbolAsync(
				new Progress<ProgressNotificationValue>(),
				symbol: "Shared.Widget.Describe",
				newName: "Explain",
				filePath: null,
				workspace: fixture.SolutionPath,
				apply: true,
				cancellationToken: cancellationToken);

			var notice = renamed.Notices
				.Where(notice => notice.Contains("Repo.Installer.slnx", StringComparison.Ordinal))
				.ShouldHaveSingleItem();

			notice.ShouldContain("not open (its worker stopped: Evicted)");
		}
	}

	/// <summary>
	/// The key of a workspace whose worker was evicted, sent under the wrong argument, is refused as
	/// the misread argument it is -- without calling the workspace loaded, because nothing is, and the
	/// call that follows the advice pays a full load. Sent as <c>workspaceKey</c> it still names the
	/// workspace, since the row that remembers its path is still there.
	/// </summary>
	[Test]
	public async Task A_key_sent_as_workspace_for_an_evicted_worker_is_not_called_loaded()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		// Idle is decided on a clock the test moves, so the worker is evicted when the test says and not
		// during a stall of a loaded machine before the loaded half has been asserted.
		var clock = new SteerableClock();
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromMinutes(30);
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
		});

		await manager.CallAsync<WorkspaceStatusReport>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.WorkspaceStatus,
			NoArguments,
			retryIfWorkerDied: true,
			cancellationToken);

		var worker = manager.Workers.ShouldHaveSingleItem();
		var misread = WorkspaceHints.From(RootedPath.Absolute(Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, worker.Key)));

		var whileLoaded = Should.Throw<McpException>(() => manager.WorkspaceFor(misread));
		whileLoaded.Message.ShouldContain("which is loaded");

		// After the load has filed its finish, which restarts the idle clock: a jump before it would be
		// undone by the restart, and the worker would never be idle past the limit.
		await WaitUntilAsync(() => worker.LoadDuration is not null, TimeSpan.FromSeconds(30), cancellationToken);
		clock.Jump(TimeSpan.FromHours(1));
		await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(60), cancellationToken);
		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);

		var afterEviction = Should.Throw<McpException>(() => manager.WorkspaceFor(misread));
		afterEviction.Message.ShouldContain($"It is the key of {worker.SolutionPath}");
		afterEviction.Message.ShouldContain("has stopped (Evicted)");
		afterEviction.Message.ShouldNotContain("which is loaded");

		manager.WorkspaceFor(WorkspaceHints.From(null, worker.Key)).ShouldBe(worker.SolutionPath);
	}

	/// <summary>
	/// A call holds its worker from the moment it is handed over to the moment it is called. In between,
	/// nothing is running on the worker yet, so the hold is the only thing that says somebody is about
	/// to use it -- and a sweep that lands there, with its clock far past the idle limit, must leave it.
	/// <para>
	/// Real timing practically never puts a sweep in that instant, so the test makes one: the worker's
	/// "Forwarding" log line is written after the call has its worker and before its activity begins,
	/// and the logger pauses there while the clock jumps an hour and the sweep runs. That nothing is
	/// running on the worker at the pause is asserted, so a line that moved after the activity began
	/// would fail here rather than leave the test proving nothing.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_call_holds_its_worker_between_being_handed_it_and_calling_it()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var clock = new SteerableClock();
		using var pause = new Pause();
		var armed = 0;

		using var logs = new ListeningLoggerFactory((category, message) =>
		{
			var isTheCall = Volatile.Read(ref armed) == 1
				&& category.EndsWith(nameof(WorkspaceWorker), StringComparison.Ordinal)
				&& message.StartsWith("Forwarding ", StringComparison.Ordinal);
			if (!isTheCall || !pause.TryTake()) return;

			clock.Jump(TimeSpan.FromHours(1));
			pause.Wait();
		});

		await using var manager = CreateManager(
			loggerFactory: logs,
			configure: options =>
			{
				options.IdleEvictionAfter = TimeSpan.FromMinutes(30);
				options.EvictionSweepInterval = SweepInterval;
				options.TimeProvider = clock;
			});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));
		var worker = await manager.GetOrStartAsync(hints, cancellationToken);
		await WaitUntilAsync(() => worker.LoadDuration is not null, TimeSpan.FromMinutes(2), cancellationToken);

		Volatile.Write(ref armed, 1);

		// On another thread, because the pause is a synchronous wait inside the call.
		var call = Task.Run(
			() => manager.CallAsync<WorkspaceStatusReport>(
				hints, ToolNames.WorkspaceStatus, NoArguments, retryIfWorkerDied: false, cancellationToken),
			cancellationToken);

		await pause.Reached.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

		var runningAtPause = manager.Describe().ShouldHaveSingleItem().Running.Count;
		await Task.Delay(SweepInterval * 8, cancellationToken);
		var survived = worker.IsAlive;
		var note = EvictionNote(manager);

		pause.Release();

		runningAtPause.ShouldBe(0, "the pause has to be before the call's activity begins, or the activity keeps the worker and the hold is not what is tested");
		survived.ShouldBeTrue("a worker handed to a call must not be evicted before the call reaches it");
		note.ShouldBeNull();

		(await call).WorkspaceKey.ShouldBe(worker.Key);
		manager.Workers.ShouldHaveSingleItem().ShouldBeSameAs(worker);
	}

	/// <summary>
	/// The report that ends a load moves the worker out of Loading before the idle clock restarts, and
	/// the worker holds itself across that gap. A sweep in it, an hour past the limit, sees a loaded
	/// worker idle since its process started, and only the hold stops it evicting a solution the moment
	/// it finished loading.
	/// <para>
	/// The gap is a few instructions, so the test makes it wide: the restart is the one clock read the
	/// load makes, and the clock pauses that read -- picked out by <c>FollowLoadAsync</c> being on the
	/// stack -- while it jumps an hour and the sweep runs. The worker being loaded with nothing running
	/// at the pause is asserted, so the pause is known to be in the gap the hold covers.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_worker_whose_load_just_finished_is_not_evicted_before_its_clock_restarts()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var clock = new SteerableClock();
		using var pause = new Pause();
		WorkspaceWorker? watched = null;

		clock.OnRead = () =>
		{
			var isTheRestart = Volatile.Read(ref watched) is not null
				&& Environment.StackTrace.Contains("FollowLoadAsync", StringComparison.Ordinal);
			if (!isTheRestart || !pause.TryTake()) return;

			clock.Jump(TimeSpan.FromHours(1));
			pause.Wait();
		};

		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromMinutes(30);
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
		});

		var worker = await manager.GetOrStartAsync(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), cancellationToken);
		Volatile.Write(ref watched, worker);

		await pause.Reached.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);

		var stateAtPause = worker.State;
		var runningAtPause = manager.Describe().ShouldHaveSingleItem().Running.Count;
		await Task.Delay(SweepInterval * 8, cancellationToken);
		var survived = worker.IsAlive;
		var note = EvictionNote(manager);

		pause.Release();

		stateAtPause.ShouldBe(WorkspaceState.Loaded);
		runningAtPause.ShouldBe(0, "the load's own activity has to have ended, or it keeps the worker and the hold is not what is tested");
		survived.ShouldBeTrue("a worker must not be evicted between its load finishing and its idle clock restarting");
		note.ShouldBeNull();

		await WaitUntilAsync(() => worker.LoadDuration is not null, TimeSpan.FromSeconds(30), cancellationToken);
		worker.IsAlive.ShouldBeTrue();
	}

	/// <summary>
	/// The real clock, moved forward on demand, with a hook on every read. Timers stay real, so the
	/// sweep still ticks at its interval; only what it takes "now" to be is steered.
	/// </summary>
	private sealed class SteerableClock : TimeProvider
	{
		private long _offsetTicks;

		/// <summary>Called on every read of the time, before it is taken.</summary>
		public Action? OnRead { get; set; }

		public void Jump(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

		public override DateTimeOffset GetUtcNow()
		{
			OnRead?.Invoke();
			return base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
		}
	}

	/// <summary>One place a thread stops until the test lets it go, taken by the first caller only.</summary>
	private sealed class Pause : IDisposable
	{
		private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ManualResetEventSlim _released = new();
		private int _taken;

		public Task Reached => _reached.Task;

		public bool TryTake() => Interlocked.Exchange(ref _taken, 1) == 0;

		/// <summary>Stops the calling thread until released, bounded so a failed test cannot hang the suite.</summary>
		public void Wait()
		{
			_reached.TrySetResult();
			_released.Wait(TimeSpan.FromMinutes(1));
		}

		public void Release() => _released.Set();

		public void Dispose()
		{
			_released.Set();
			_released.Dispose();
		}
	}

	/// <summary>Hands every log line, with its category, to a callback, synchronously on the logging thread.</summary>
	private sealed class ListeningLoggerFactory(Action<string, string> onMessage) : ILoggerFactory
	{
		public ILogger CreateLogger(string categoryName) => new Listener(categoryName, onMessage);

		public void AddProvider(ILoggerProvider provider)
		{
		}

		public void Dispose()
		{
		}

		private sealed class Listener(string category, Action<string, string> onMessage) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state)
				where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter) =>
				onMessage(category, formatter(state, exception));
		}
	}

	/// <summary>
	/// The eviction's row in the history the tray reads, or null before there is one. Waited on
	/// rather than the worker's exit reason, which is set a moment before the row is filed.
	/// </summary>
	private static WorkerActivity? EvictionNote(WorkspaceManager manager) =>
		manager.Describe()
			.SelectMany(summary => summary.Recent)
			.FirstOrDefault(activity => activity.Operation == WorkspaceManager.EvictOperation);

	private static bool ProcessHasExited(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return process.HasExited;
		}
		catch (ArgumentException)
		{
			return true;
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + timeout;

		while (!condition())
		{
			if (DateTime.UtcNow > deadline) throw new TimeoutException($"The condition did not hold within {timeout}.");

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}
	}
}
