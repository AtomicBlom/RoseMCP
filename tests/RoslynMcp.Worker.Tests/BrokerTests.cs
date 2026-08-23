using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoslynMcp.Broker;
using RoslynMcp.Contracts;

namespace RoslynMcp.Worker.Tests;

/// <summary>
/// Integration tests that spawn real worker processes, because the things worth checking here --
/// that a solution is loaded once, that a dead worker is replaced, that nothing is orphaned -- are
/// all properties of process lifetime.
/// </summary>
public sealed class BrokerTests
{
	/// <summary>
	/// The whole point of the broker. If a second call reloads the solution, everything else it
	/// does is wasted effort.
	/// </summary>
	[Fact]
	public async Task Reuses_one_warm_worker_across_calls()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var first = await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);
		var status = await first.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, new Dictionary<string, object?>(), TestContext.Current.CancellationToken);

		Assert.Equal(WorkspaceState.Loaded, status.State);

		// Resolve from a source file this time; it must land on the same worker.
		var second = await manager.GetOrStartAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current.CancellationToken);

		Assert.Same(first, second);
		Assert.Single(manager.Workers);
	}

	[Fact]
	public async Task Restart_replaces_the_worker_process()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var before = await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);
		var after = await manager.RestartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);

		Assert.NotSame(before, after);
		Assert.Equal(WorkerExitReason.StoppedByBroker, before.ExitReason);
		Assert.True(after.IsAlive);
		Assert.Single(manager.Workers);
	}

	[Fact]
	public async Task Keeps_separate_workers_for_separate_solutions()
	{
		using var simple = FixtureSolution.Copy("Simple", "Simple.sln");
		using var generator = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(simple.SolutionPath, TestContext.Current.CancellationToken);
		await manager.GetOrStartAsync(generator.SolutionPath, TestContext.Current.CancellationToken);

		Assert.Equal(2, manager.Workers.Count);

		// With more than one open, an unqualified call cannot be guessed at.
		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => manager.GetOrStartAsync(null, TestContext.Current.CancellationToken));

		Assert.Contains("workspace argument is required", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Infers_the_workspace_when_only_one_is_open()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var opened = await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);
		var inferred = await manager.GetOrStartAsync(null, TestContext.Current.CancellationToken);

		Assert.Same(opened, inferred);
	}

	[Fact]
	public async Task Closing_stops_the_worker_and_forgets_it()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);

		Assert.True(await manager.CloseAsync(fixture.SolutionPath, TestContext.Current.CancellationToken));
		Assert.Empty(manager.Workers);
		Assert.False(worker.IsAlive);

		// Closing something that is not open is a no-op, not an error.
		Assert.False(await manager.CloseAsync(fixture.SolutionPath, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Refuses_a_solution_that_is_not_there()
	{
		await using var manager = CreateManager();

		var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}", "Nope.sln");

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => manager.GetOrStartAsync(missing, TestContext.Current.CancellationToken));

		// Naming the path matters: this is also what a caller sees after a branch switch removes
		// the solution out from under them.
		Assert.Contains(missing, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(manager.Workers);
	}

	/// <summary>
	/// Memory is sampled from the process table, not self-reported, so the tray keeps showing real
	/// numbers for a worker that has stopped answering.
	/// </summary>
	[Fact]
	public async Task Reports_process_and_memory_for_each_workspace()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);

		var summary = Assert.Single(manager.Describe());

		Assert.Equal("Simple", summary.DisplayName);
		Assert.True(summary.Alive);
		Assert.Equal("Running", summary.ExitReason);
		Assert.NotNull(summary.ProcessId);
		Assert.NotEqual(Environment.ProcessId, summary.ProcessId);

		// A Roslyn host is never this small; a zero here would mean we sampled the wrong thing.
		Assert.True(summary.WorkingSetBytes > 1_000_000, $"working set was {summary.WorkingSetBytes}");
		Assert.True(summary.ManagedHeapBytes > 0);
		Assert.True(summary.Uptime > TimeSpan.Zero);
	}

	private static WorkspaceManager CreateManager() => new(
		Options.Create(new BrokerOptions()),
		NullLoggerFactory.Instance,
		NullLogger<WorkspaceManager>.Instance);
}
