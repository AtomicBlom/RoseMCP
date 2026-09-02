using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Integration tests that spawn real worker processes, because the things worth checking here --
/// that a solution is loaded once, that a dead worker is replaced, that nothing is orphaned -- are
/// all properties of process lifetime.
/// </summary>
public sealed class BrokerTests
{
	/// <summary>
	/// MSBuild properties are fixed when a workspace opens, so asking for different ones is a
	/// restart -- and the restart has to actually carry them to the new process.
	/// </summary>
	[Fact]
	public async Task Reloads_under_the_properties_asked_for()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);

		var restarted = await manager.RestartAsync(
			fixture.SolutionPath,
			TestContext.Current.CancellationToken,
			WorkspaceBuildOverrides.From("Release", null, null));

		var status = await restarted.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, new Dictionary<string, object?>(), TestContext.Current.CancellationToken);

		Assert.Equal("Release|AnyCPU", status.BuildConfiguration);
	}

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

		// With more than one open, an unqualified call cannot be guessed at. Http mode makes that
		// ordinary rather than exotic: one broker serves every repository on the machine, so a
		// second solution loading in another session is enough to make every zero-argument call
		// ambiguous.
		//
		// McpException, not ArgumentException, and that is not a detail. The SDK renders an
		// exception it does not recognise as "An error occurred invoking 'rose_diagnostics'." and
		// throws the message away, leaving a caller that could have corrected the call itself with
		// nothing to go on. This one names both open solutions, because picking one is the fix.
		var error = await Assert.ThrowsAsync<McpException>(
			() => manager.GetOrStartAsync(null, TestContext.Current.CancellationToken));

		Assert.Contains("workspace argument is required", error.Message, StringComparison.Ordinal);
		Assert.Contains(simple.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(generator.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
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

	/// <summary>
	/// A tool that needs a setup call before it answers anything is a tool that gets skipped in
	/// favour of grep, so the zero-argument path has to find the solution on its own.
	/// </summary>
	[Fact]
	public async Task Finds_a_solution_from_the_working_directory_with_no_arguments()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager(Path.GetDirectoryName(fixture.SolutionPath)!);

		// No open call, no path.
		var worker = await manager.GetOrStartAsync(null, TestContext.Current.CancellationToken);

		Assert.Equal(fixture.SolutionPath, worker.SolutionPath, ignoreCase: true);
	}
	/// <summary>
	/// McpException, not ArgumentException, and that is not a detail. The SDK renders an exception
	/// it does not recognise as "An error occurred invoking 'rose_workspace_status'." and throws the
	/// message away, so a caller that could have corrected the call itself learns nothing.
	/// </summary>
	[Fact]
	public async Task Says_where_it_looked_when_there_is_no_solution_to_find()
	{
		var nowhere = NowhereDirectory.Path();
		await using var manager = CreateManager(nowhere);

		var error = await Assert.ThrowsAsync<McpException>(
			() => manager.GetOrStartAsync(null, TestContext.Current.CancellationToken));

		Assert.Contains(nowhere, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The window's state, configuration and project count come from here, and none of them needs
	/// a client to have called anything: the status the broker asks for on connect is kept, and it
	/// is the same call a client would have made.
	/// </summary>
	[Fact]
	public async Task Describes_the_load_it_followed_without_being_asked()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);

		// The load time is the last thing recorded, so once it is there the rest is too.
		var loaded = await WaitForAsync(
			() => manager.Describe().Single() is { LoadSeconds: not null } summary ? summary : null,
			TimeSpan.FromMinutes(2));

		Assert.Equal(WorkspaceState.Loaded, loaded.State);
		Assert.Equal("Debug|AnyCPU", loaded.BuildConfiguration);
		Assert.Equal(2, loaded.ProjectCount);
		Assert.Empty(loaded.FailedProjects);
		Assert.Empty(loaded.DegradedReasons);
		Assert.True(loaded.LoadSeconds > 0);
	}

	/// <summary>Waits for something to show up, since the load is followed on another thread.</summary>
	private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout)
		where T : class
	{
		var deadline = DateTime.UtcNow + timeout;

		while (DateTime.UtcNow < deadline)
		{
			if (probe() is { } found) return found;

			await Task.Delay(100, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException($"Nothing showed up within {timeout.TotalSeconds:F0}s.");
	}

	/// <summary>
	/// The failure that started this said "An error occurred invoking 'rose_rename_symbol'." and
	/// nothing else, because the SDK drops the message of an exception it does not recognise. The
	/// tool knew exactly what was wrong; a caller looking at the wrong workspace could not tell.
	/// </summary>
	[Fact]
	public async Task A_failing_tool_says_what_went_wrong_and_where()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var elsewhere = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}", "Nowhere.cs");

		var error = await Assert.ThrowsAnyAsync<Exception>(() => manager.CallAsync<SymbolInfoResult>(
			fixture.SolutionPath,
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["filePath"] = elsewhere, ["line"] = 1, ["column"] = 1 },
			retryIfWorkerDied: true,
			TestContext.Current.CancellationToken));

		Assert.Contains(elsewhere, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(fixture.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A result that does not name its workspace cannot be checked: nothing found in the wrong
	/// solution is indistinguishable from nothing to find in the right one. The broker fills this
	/// in for every result type, so it is asserted through the same path the tools use.
	/// </summary>
	[Fact]
	public async Task Every_result_says_which_workspace_answered()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var result = await manager.CallAsync<SymbolSearchResult>(
			fixture.SolutionPath,
			ToolNames.SearchSymbols,
			new Dictionary<string, object?> { ["query"] = "Calculator" },
			retryIfWorkerDied: true,
			TestContext.Current.CancellationToken);

		Assert.Equal(fixture.SolutionPath, result.Workspace, ignoreCase: true);
		Assert.StartsWith("Simple-", result.WorkspaceKey, StringComparison.Ordinal);
	}

	/// <summary>
	/// The lifecycle tools hold their worker before they ask it anything, so they do not route
	/// through CallAsync and were left unattributed -- status of all tools answering "which
	/// workspace is this?" without naming it.
	/// </summary>
	[Fact]
	public async Task Workspace_status_names_its_workspace_too()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);
		var status = await manager.StatusOfAsync(worker, TestContext.Current.CancellationToken);

		Assert.Equal(fixture.SolutionPath, status.Workspace, ignoreCase: true);
		Assert.Equal(worker.Key, status.WorkspaceKey);
	}

	/// <summary>
	/// The key has to outlive the process it names, or a caller holding one across a reload -- which
	/// happens for ordinary reasons -- would be told its workspace no longer exists.
	/// </summary>
	[Fact]
	public async Task The_workspace_key_survives_the_worker_being_replaced()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var before = await manager.GetOrStartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);
		var key = before.Key;

		var after = await manager.RestartAsync(fixture.SolutionPath, TestContext.Current.CancellationToken);

		Assert.NotEqual(before.ProcessId, after.ProcessId);
		Assert.Equal(key, after.Key);
	}

	private static WorkspaceManager CreateManager(string? defaultRoot = null) => new(
		Options.Create(new BrokerOptions
		{
			// Somewhere with no solution, unless a test is specifically exercising discovery.
			DefaultWorkspaceRoot = defaultRoot ?? Path.GetTempPath(),
		}),
		NullLoggerFactory.Instance,
		NullLogger<WorkspaceManager>.Instance);
}
