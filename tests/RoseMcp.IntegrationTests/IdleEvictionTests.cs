using System.Diagnostics;

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
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromSeconds(5);
			options.EvictionSweepInterval = SweepInterval;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		await manager.CallAsync<WorkspaceStatusReport>(
			hints, ToolNames.WorkspaceStatus, NoArguments, retryIfWorkerDied: true, cancellationToken);

		var worker = manager.Workers.ShouldHaveSingleItem();
		var processId = worker.ProcessId.ShouldNotBeNull();

		// Status is a look, not a use: it must not restart the idle clock.
		var usedBefore = worker.LastUsedUtc;
		await manager.StatusAsync(hints, cancellationToken);
		worker.LastUsedUtc.ShouldBe(usedBefore);

		var warm = manager.List().Workspaces.ShouldHaveSingleItem();
		warm.WorkspaceKey.ShouldBe(worker.Key);
		warm.ExitReason.ShouldBeNull();
		warm.State.ShouldBe(WorkspaceState.Loaded);
		manager.List().IdleEvictionAfter.ShouldBe(TimeSpan.FromSeconds(5));

		await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(60), cancellationToken);

		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);
		EvictionNote(manager).ShouldNotBeNull().Message.ShouldNotBeNull().ShouldContain("Unused for");

		// Listed, and described to the tray, as evicted, with the reason in its history.
		var evicted = manager.List().Workspaces.ShouldHaveSingleItem();
		evicted.ExitReason.ShouldBe(nameof(WorkerExitReason.Evicted));
		evicted.State.ShouldBe(WorkspaceState.Unloaded);

		manager.Describe().ShouldHaveSingleItem().Alive.ShouldBeFalse();

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
		await using var manager = CreateManager(configure: options =>
		{
			options.IdleEvictionAfter = TimeSpan.FromSeconds(20);
			options.SolutionGoneGrace = TimeSpan.FromMilliseconds(500);
			options.EvictionSweepInterval = SweepInterval;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		await manager.CallAsync<WorkspaceStatusReport>(
			hints, ToolNames.WorkspaceStatus, NoArguments, retryIfWorkerDied: true, cancellationToken);

		var worker = manager.Workers.ShouldHaveSingleItem();

		File.Delete(fixture.SolutionPath);

		await WaitUntilAsync(() => EvictionNote(manager) is not null, TimeSpan.FromSeconds(15), cancellationToken);

		worker.ExitReason.ShouldBe(WorkerExitReason.Evicted);
		EvictionNote(manager).ShouldNotBeNull().Message.ShouldNotBeNull().ShouldContain("missing");

		await WaitUntilAsync(() => manager.Workers.Count == 0, TimeSpan.FromSeconds(60), cancellationToken);

		manager.List().Workspaces.ShouldBeEmpty();
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
