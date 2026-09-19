using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;


using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Which solution a call means, and which workspace answers it: opening without waiting for the
/// load, reusing one warm worker, keeping separate solutions apart, reloading under the properties
/// asked for, and finding a solution from the working directory when nobody named one. These spawn
/// real worker processes, because a solution being loaded once is a property of process lifetime.
/// <para>
/// The refusals matter as much as the answers. A session that did not open a workspace is not served
/// from one another session left open, and a bare call with nothing to find says where it looked --
/// both being ways to get a confident answer about the wrong code.
/// </para>
/// </summary>
public sealed class BrokerTests
{
	/// <summary>
	/// Starting a load without waiting for it (#44). A large solution takes about two minutes, and
	/// every second of that used to be a session blocked on a call, with nothing it could usefully do
	/// instead.
	/// <para>
	/// This is <c>rose_workspace_open</c> rather than a tool of its own, and the pairing is the point:
	/// open had been <c>rose_workspace_status</c> under a second name, down to the same two lines of
	/// body, which is why its description had to admit it was "rarely needed on its own". Not waiting
	/// is what gives it something to be.
	/// </para>
	/// <para>
	/// What is asserted is the contract that can be asserted: the tool answers about the solution it
	/// resolved, asking twice starts one worker rather than two, and a real question afterwards still
	/// gets a real answer -- which is the claim that makes starting early safe. The non-blocking
	/// property itself is structural rather than timed, because it comes from the tool reading only
	/// broker-side state and never calling the worker; a stopwatch here would be measuring how long a
	/// two-project fixture takes to load, which is not the thing under test and would fail on a busy
	/// machine for a reason that is not a bug.
	/// </para>
	/// </summary>
	[Test]
	public async Task Opening_a_workspace_answers_without_waiting_for_the_load()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();
		var tools = new RoseMcp.Broker.Tools.BrokerTools(manager);

		var started = await tools.OpenAsync(fixture.SolutionPath, TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(fixture.SolutionPath, started.Workspace);
		Assert.NotEmpty(started.WorkspaceKey);
		Assert.True(started.Alive, $"the worker should be alive; exit reason was '{started.ExitReason}'");

		// Polling is the same call, so it must not start a second worker.
		var polled = await tools.OpenAsync(fixture.SolutionPath, TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(started.ProcessId, polled.ProcessId);
		Assert.Single(manager.Workers);

		// And the promise that makes starting early safe: every other tool still blocks until the
		// workspace can answer, so a question asked immediately gets a real answer rather than a
		// half-loaded one.
		var status = await tools.StatusAsync(
			new Progress<ProgressNotificationValue>(), fixture.SolutionPath, TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(WorkspaceState.Loaded, status.State);
		Assert.NotEmpty(status.Projects);

		// Loaded now, so opening again has nothing to wait for and says nothing about waiting. The
		// notice belongs to the loading answer alone: told unconditionally it would read as "still
		// working" on a workspace that is finished, which is the one thing a caller polling for
		// completion must not be told.
		var settled = await tools.OpenAsync(fixture.SolutionPath, TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(WorkspaceState.Loaded, settled.State);
		Assert.DoesNotContain(settled.Notices, notice => notice.Contains("Still loading", StringComparison.Ordinal));
	}

	/// <summary>
	/// A loading answer says what will happen next, because nothing will happen on its own.
	/// <para>
	/// Pushing the completion where the caller could act on it -- the shape the caller actually wants,
	/// a result that arrives when the load finishes -- needs <c>notifications/claude/channel</c>. That
	/// is client-specific rather than MCP, is in research preview behind an organisation policy, and
	/// is dropped silently where it is not enabled, with no error returned to the server. So this tool
	/// cannot know whether a promise to notify would be kept, and a promise that can be silently
	/// broken is worse than none. What it can do is say that calling again is the mechanism, which is
	/// where the answer would have arrived anyway: channel events are delivered on the caller's next
	/// turn, not mid-turn.
	/// </para>
	/// <para>
	/// Asserted on the fact rather than the wording -- that a still-loading answer carries advice and
	/// a finished one does not -- since the sentence is meant to be rewritten as it is read in
	/// practice, and a test that pins prose stops that happening.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_loading_answer_says_how_to_find_out_it_has_finished()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();
		var tools = new RoseMcp.Broker.Tools.BrokerTools(manager);

		var opened = await tools.OpenAsync(fixture.SolutionPath, TestContext.Current!.Execution.CancellationToken);

		// A two-project fixture can be loaded before the first call returns, and that is a legitimate
		// outcome of this tool rather than a flake -- so the assertion is on the pairing of state with
		// the fields that state can fill, which holds either way and is what makes the state worth
		// reporting instead of a null. Asserting the loading notice alone would leave the finished half
		// of the pairing untested on every machine fast enough to skip it.
		if (opened.State == WorkspaceState.Loading)
		{
			Assert.Contains(opened.Notices, notice => notice.Contains("rose_workspace_open", StringComparison.Ordinal));

			Assert.Null(opened.ProjectCount);
			Assert.Null(opened.LoadSeconds);
		}
		else
		{
			Assert.Equal(WorkspaceState.Loaded, opened.State);

			Assert.NotNull(opened.ProjectCount);
			Assert.NotNull(opened.LoadSeconds);
		}
	}

	/// <summary>
	/// MSBuild properties are fixed when a workspace opens, so asking for different ones is a
	/// restart -- and the restart has to actually carry them to the new process.
	/// </summary>
	[Test]
	public async Task Reloads_under_the_properties_asked_for()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		var restarted = await manager.RestartAsync(
			WorkspaceHints.From(fixture.SolutionPath),
			TestContext.Current!.Execution.CancellationToken,
			WorkspaceBuildOverrides.From("Release", null, null));

		var status = await restarted.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, new Dictionary<string, object?>(), TestContext.Current!.Execution.CancellationToken);

		Assert.Equal("Release|AnyCPU", status.BuildConfiguration);
	}

	/// <summary>
	/// The whole point of the broker. If a second call reloads the solution, everything else it
	/// does is wasted effort.
	/// </summary>
	[Test]
	public async Task Reuses_one_warm_worker_across_calls()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var first = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);
		var status = await first.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, new Dictionary<string, object?>(), TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(WorkspaceState.Loaded, status.State);

		// Resolve from a source file this time; it must land on the same worker.
		var second = await manager.GetOrStartAsync(
			WorkspaceHints.From(fixture.Path("Simple", "Core", "Calculator.cs")),
			TestContext.Current!.Execution.CancellationToken);

		Assert.Same(first, second);
		Assert.Single(manager.Workers);
	}

	[Test]
	public async Task Keeps_separate_workers_for_separate_solutions()
	{
		using var simple = FixtureSolution.Copy("Simple", "Simple.sln");
		using var generator = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(WorkspaceHints.From(simple.SolutionPath), TestContext.Current!.Execution.CancellationToken);
		await manager.GetOrStartAsync(WorkspaceHints.From(generator.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(2, manager.Workers.Count);

		// A call with nothing to go on is resolved from where the asking came from, and this manager
		// is rooted somewhere with no solution -- so it fails, and names both what it looked at and
		// what is already loaded, because naming one of those is the fix.
		//
		// McpException, not ArgumentException, and that is not a detail. The SDK renders an
		// exception it does not recognise as "An error occurred invoking 'rose_diagnostics'." and
		// throws the message away, leaving a caller that could have corrected the call itself with
		// nothing to go on.
		var error = await Assert.ThrowsAsync<McpException>(
			() => manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current!.Execution.CancellationToken));

		Assert.Contains("workspace argument", error.Message, StringComparison.Ordinal);
		Assert.Contains(simple.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(generator.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The guess this replaces: a bare call used to be answered from the single open worker, which is
	/// not a fact about the question but about what somebody else did earlier. One broker serves every
	/// repository on the machine, so "the only one open" is routinely another session's solution --
	/// and an answer from the wrong compilation is indistinguishable from a true negative.
	/// </summary>
	[Test]
	public async Task Does_not_answer_from_a_workspace_another_session_left_open()
	{
		using var open = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		using var mine = FixtureSolution.Copy("Simple", "Simple.sln");

		// Rooted where this session actually is, with the other solution already loaded.
		await using var manager = CreateManager(Path.GetDirectoryName(mine.SolutionPath)!);

		var theirs = await manager.GetOrStartAsync(
			WorkspaceHints.From(open.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		var bare = await manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current!.Execution.CancellationToken);

		Assert.NotSame(theirs, bare);
		Assert.Equal(mine.SolutionPath, bare.SolutionPath, ignoreCase: true);
	}

	/// <summary>
	/// And with nothing to resolve from, a loaded workspace is still not an answer -- it is only a
	/// suggestion in the failure.
	/// </summary>
	[Test]
	public async Task Refuses_rather_than_borrowing_the_only_open_workspace()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(
			WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		var error = await Assert.ThrowsAsync<McpException>(
			() => manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current!.Execution.CancellationToken));

		Assert.Contains(fixture.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Test]
	public async Task Closing_stops_the_worker_and_forgets_it()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		var closed = await manager.CloseAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		Assert.True(closed.Closed);
		Assert.Equal(fixture.SolutionPath, closed.Workspace);
		Assert.NotEmpty(closed.WorkspaceKey);
		Assert.Empty(manager.Workers);
		Assert.False(worker.IsAlive, "closing the workspace stops its worker");

		// Closing something that is not open is a no-op, not an error.
		Assert.False((await manager.CloseAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken)).Closed);
	}

	[Test]
	public async Task Refuses_a_solution_that_is_not_there()
	{
		await using var manager = CreateManager();

		var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}", "Nope.sln");

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => manager.GetOrStartAsync(WorkspaceHints.From(missing), TestContext.Current!.Execution.CancellationToken));

		// Naming the path matters: this is also what a caller sees after a branch switch removes
		// the solution out from under them.
		Assert.Contains(missing, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(manager.Workers);
	}

	/// <summary>
	/// Memory is sampled from the process table, not self-reported, so the tray keeps showing real
	/// numbers for a worker that has stopped answering.
	/// </summary>
	[Test]
	public async Task Reports_process_and_memory_for_each_workspace()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

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
	[Test]
	public async Task Finds_a_solution_from_the_working_directory_with_no_arguments()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager(Path.GetDirectoryName(fixture.SolutionPath)!);

		// No open call, no path.
		var worker = await manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(fixture.SolutionPath, worker.SolutionPath, ignoreCase: true);
	}

	/// <summary>
	/// McpException, not ArgumentException, and that is not a detail. The SDK renders an exception
	/// it does not recognise as "An error occurred invoking 'rose_workspace_status'." and throws the
	/// message away, so a caller that could have corrected the call itself learns nothing.
	/// </summary>
	[Test]
	public async Task Says_where_it_looked_when_there_is_no_solution_to_find()
	{
		var nowhere = NowhereDirectory.Path();
		await using var manager = CreateManager(nowhere);

		var error = await Assert.ThrowsAsync<McpException>(
			() => manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current!.Execution.CancellationToken));

		Assert.Contains(nowhere, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The window's state, configuration and project count come from here, and none of them needs
	/// a client to have called anything: the status the broker asks for on connect is kept, and it
	/// is the same call a client would have made.
	/// </summary>
	[Test]
	public async Task Describes_the_load_it_followed_without_being_asked()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

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

			await Task.Delay(100, TestContext.Current!.Execution.CancellationToken);
		}

		throw new TimeoutException($"Nothing showed up within {timeout.TotalSeconds:F0}s.");
	}
}
