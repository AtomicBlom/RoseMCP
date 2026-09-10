using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;

using Xunit.Sdk;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Integration tests that spawn real worker processes, because the things worth checking here --
/// that a solution is loaded once, that a dead worker is replaced, that nothing is orphaned -- are
/// all properties of process lifetime.
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
	public async Task Restart_replaces_the_worker_process()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var before = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);
		var after = await manager.RestartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		Assert.NotSame(before, after);
		Assert.Equal(WorkerExitReason.StoppedByBroker, before.ExitReason);
		Assert.True(after.IsAlive);
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

		Assert.True(await manager.CloseAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken));
		Assert.Empty(manager.Workers);
		Assert.False(worker.IsAlive, "closing the workspace stops its worker");

		// Closing something that is not open is a no-op, not an error.
		Assert.False(await manager.CloseAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken));
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

	/// <summary>
	/// The failure that started this said "An error occurred invoking 'rose_rename_symbol'." and
	/// nothing else, because the SDK drops the message of an exception it does not recognise. The
	/// tool knew exactly what was wrong; a caller looking at the wrong workspace could not tell.
	/// </summary>
	[Test]
	public async Task A_failing_tool_says_what_went_wrong_and_where()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var elsewhere = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}", "Nowhere.cs");

		var error = await Assert.ThrowsAnyAsync<Exception>(() => manager.CallAsync<SymbolInfoResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["filePath"] = elsewhere, ["line"] = 1, ["column"] = 1 },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken));

		Assert.Contains(elsewhere, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(fixture.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A cancelled call is cancelled through the broker rather than only abandoned by the caller.
	/// </summary>
	/// <remarks>
	/// Nothing referenced <see cref="CancellableToolCall"/> or cancelled a worker call, so the whole
	/// mechanism was unprotected -- including the ordering it exists for, which is sending the
	/// cancellation before abandoning the wait. <c>McpClient.CallToolAsync</c> honours a token by giving
	/// up locally and never telling the far side, which is what that class was written to replace.
	/// <para>
	/// What this asserts is that the path runs and the outcome is recorded as cancelled, and that the
	/// worker is usable straight afterwards rather than wedged. What it does <em>not</em> assert is the
	/// timing the invariant is really about -- that the worker stops work sooner -- because that needs
	/// an operation slow enough for the difference to exceed the noise, and the fixture solutions are
	/// deliberately small. A timing assertion over them would pass either way.
	/// </para>
	/// </remarks>
	[Test]
	public async Task Cancelling_a_call_cancels_it_at_the_broker_and_leaves_the_worker_usable()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Loaded first, so what gets cancelled below is the analysis rather than the load behind it.
		await manager.CallAsync<WorkspaceStatusReport>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?>(),
			retryIfWorkerDied: true,
			cancellationToken);

		using var cancelling = new CancellationTokenSource();

		var slow = manager.CallAsync<DiagnosticsResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.Diagnostics,
			new Dictionary<string, object?> { ["scope"] = "solution", ["includeAnalyzers"] = true },
			retryIfWorkerDied: false,
			cancelling.Token);

		await cancelling.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

		Assert.Contains(
			manager.Activities.Recent(fixture.SolutionPath),
			activity => activity.Operation == ToolNames.Diagnostics && activity.Outcome == ActivityOutcome.Cancelled);

		// And the worker answers the next question, rather than the cancellation having taken it with it.
		var afterwards = await manager.CallAsync<WorkspaceStatusReport>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?>(),
			retryIfWorkerDied: false,
			cancellationToken);

		Assert.Equal(WorkspaceState.Loaded, afterwards.State);
	}

	/// <summary>
	/// The write tools, driven the way a client drives them: through the broker, by argument name,
	/// into a real worker process.
	/// <para>
	/// This is the only thing that catches an argument the broker spells differently from the worker
	/// it forwards to. Nothing in the type system connects the two -- the broker builds a dictionary
	/// and the worker binds it by parameter name -- so a mismatch makes the tool uncallable while
	/// every in-process test of the service behind it goes on passing.
	/// </para>
	/// </summary>
	[Test]
	public async Task Writes_C_sharp_by_symbol_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var hints = WorkspaceHints.From(fixture.SolutionPath);

		var replaced = await manager.CallAsync<MemberEditResult>(
			hints,
			ToolNames.ReplaceMember,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Greeter.Greet(string)",
				["code"] = "public string Greet(string name) => $\"{_prefix}! {name}\";",
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(replaced.Applied);
		Assert.True(replaced.Verified);
		Assert.Empty(replaced.IntroducedDiagnostics);

		var body = await manager.CallAsync<MemberEditResult>(
			hints,
			ToolNames.ReplaceBody,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Greeter.Shout(string)",
				["code"] = "return text.ToLowerInvariant();",
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(body.Applied);

		var added = await manager.CallAsync<MemberEditResult>(
			hints,
			ToolNames.AddMember,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Greeter",
				["code"] = "public int Doubled => Count * 2;",
				["after"] = "Count",
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(added.Applied);
		Assert.Equal(["Doubled"], added.Members);

		// And the file on disk carries all three, in the repository's own formatting.
		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Greeter.cs"), TestContext.Current!.Execution.CancellationToken);

		Assert.Contains("\tpublic string Greet(string name) => $\"{_prefix}! {name}\";\r\n", text, StringComparison.Ordinal);
		Assert.Contains("\tpublic int Doubled => Count * 2;\r\n", text, StringComparison.Ordinal);

		// Statements, so a block: the shape follows what was supplied rather than what was there.
		Assert.Contains(
			"\tprivate static string Shout(string text)\r\n\t{\r\n\t\treturn text.ToLowerInvariant();\r\n\t}\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>Naming a symbol rather than a position has to survive the same trip.</summary>
	[Test]
	public async Task Describes_a_named_symbol_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var info = await manager.CallAsync<SymbolInfoResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["symbol"] = "Library.Greeter.PrefixLength" },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.Equal("PrefixLength", info.Name);

		var span = Assert.Single(info.DeclarationSpans);

		Assert.Equal(2, span.LineCount);
		Assert.EndsWith("Greeter.cs", span.FilePath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Changing a signature over the wire, which is where an argument the broker spells differently
	/// would show up -- and this one has the most arguments of any tool here.
	/// </summary>
	[Test]
	public async Task Changes_a_signature_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var result = await manager.CallAsync<SignatureChangeResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.ChangeSignature,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Notifier.Notify(string)",
				["parameters"] = "string message, bool urgent",
				["arguments"] = new[] { "urgent=false" },
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);

		// The interface, the base and the override, plus the two call sites in the forwarder.
		Assert.Equal(3, result.UpdatedDeclarations.Count);
		Assert.Equal(3, result.UpdatedCallSites.Count);

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Layers.cs"), TestContext.Current!.Execution.CancellationToken);

		Assert.Contains("public override string Notify(string text, bool urgent)", text, StringComparison.Ordinal);
		Assert.Contains("notifier.Notify(message, false)", text, StringComparison.Ordinal);
	}

	/// <summary>Build freshness over the wire, so its one argument cannot drift either.</summary>
	[Test]
	public async Task Reports_build_freshness_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var report = await manager.CallAsync<BuildFreshnessReport>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.BuildFreshness,
			new Dictionary<string, object?> { ["project"] = "Core" },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		var project = Assert.Single(report.Projects);

		Assert.Equal("Core", project.Project);
		Assert.True(project.Stale, "a fresh copy has no build output at all");
		Assert.Equal(1, report.StaleCount);
	}

	/// <summary>
	/// Both halves of importing, over the wire: the argument on a write tool, and the tool of its own.
	/// </summary>
	[Test]
	public async Task Imports_a_namespace_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var hints = WorkspaceHints.From(fixture.SolutionPath);

		// The argument, on the call that writes the code needing it.
		var written = await manager.CallAsync<MemberEditResult>(
			hints,
			ToolNames.AddMember,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Greeter",
				["code"] = "public string Encoded() => Encoding.UTF8.EncodingName;",
				["usings"] = new[] { "System.Text" },
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(written.Applied);
		Assert.Empty(written.IntroducedDiagnostics);

		// And the tool of its own, which finds this one already in scope and says so.
		var again = await manager.CallAsync<UsingResult>(
			hints,
			ToolNames.AddUsing,
			new Dictionary<string, object?>
			{
				["filePath"] = fixture.Path("Members", "Library", "Greeter.cs"),
				["namespaces"] = new[] { "System.Text" },
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.Empty(again.Added);
		Assert.False(again.Applied, "the second call finds the import already there");
		Assert.Contains(again.AlreadyInScope, reason => reason.Contains("already imported here", StringComparison.Ordinal));
	}

	/// <summary>
	/// A result that does not name its workspace cannot be checked: nothing found in the wrong
	/// solution is indistinguishable from nothing to find in the right one. The broker fills this
	/// in for every result type, so it is asserted through the same path the tools use.
	/// </summary>
	[Test]
	public async Task Every_result_says_which_workspace_answered()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var result = await manager.CallAsync<SymbolSearchResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.SearchSymbols,
			new Dictionary<string, object?> { ["query"] = "Calculator" },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(fixture.SolutionPath, result.Workspace, ignoreCase: true);
		Assert.StartsWith("Simple-", result.WorkspaceKey, StringComparison.Ordinal);
	}

	/// <summary>
	/// The lifecycle tools hold their worker before they ask it anything, so they do not route
	/// through CallAsync and were left unattributed -- status of all tools answering "which
	/// workspace is this?" without naming it.
	/// </summary>
	[Test]
	public async Task Workspace_status_names_its_workspace_too()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);
		var status = await manager.StatusOfAsync(worker, TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(fixture.SolutionPath, status.Workspace, ignoreCase: true);
		Assert.Equal(worker.Key, status.WorkspaceKey);
	}

	/// <summary>
	/// The key has to outlive the process it names, or a caller holding one across a reload -- which
	/// happens for ordinary reasons -- would be told its workspace no longer exists.
	/// </summary>
	[Test]
	public async Task The_workspace_key_survives_the_worker_being_replaced()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var before = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);
		var key = before.Key;

		var after = await manager.RestartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		Assert.NotEqual(before.ProcessId, after.ProcessId);
		Assert.Equal(key, after.Key);
	}

	/// <summary>
	/// A stdio server exits when its client's stdin closes, and takes its workers with it.
	/// </summary>
	/// <remarks>
	/// "Workers die with the broker" covered the Roslyn workers and nothing covered the process
	/// holding them, and six servers were once seen accumulating over one session, one per reconnect.
	/// <para>
	/// This is a regression test rather than the fix for that: the behaviour it asserts already held
	/// when it was written, in every shape that could be arranged -- idle, after a handshake and a
	/// tool listing, mid-call, relaying to a tray, and relaying to a tray that had been killed. What
	/// is left of the report is a client that never closes stdin at all, and no rule inside this
	/// process reaches that.
	/// </para>
	/// <para>
	/// Mid-call is the shape asserted, because it is the one with something to go wrong: the call has
	/// a worker loading a solution behind it, so exiting means ending work in flight rather than
	/// noticing an idle stream. The wait is generous for the same reason -- what is under test is
	/// that it ends, not how quickly.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_stdio_server_exits_when_its_client_closes_stdin()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		// A port with no tray on it, so this server owns its workers rather than relaying to whatever
		// happens to be running on this machine.
		using var server = RoseServerProcess.Start("--port", RoseServerProcess.FreePort().ToString());

		await server.InitializeAsync(cancellationToken);

		var workersBefore = WorkerProcessIds();

		// Not awaited: the point is to close stdin while this is still running.
		var call = server.CallToolAsync(
			ToolNames.WorkspaceStatus,
			$$"""{"workspace":{{JsonSerializer.Serialize(fixture.SolutionPath)}}}""",
			cancellationToken);

		var worker = await WaitForNewWorkerAsync(workersBefore, cancellationToken);

		server.CloseStandardInput();

		Assert.True(
			await server.WaitForExitAsync(TimeSpan.FromMinutes(2), cancellationToken),
			$"the server (pid {server.Id}) was still running two minutes after its client's stdin closed");

		// And the worker goes with it, which is the invariant this extends rather than replaces.
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (WorkerProcessIds().Contains(worker) && DateTime.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		}

		Assert.DoesNotContain(worker, WorkerProcessIds());

		// Whether the in-flight call was answered is a race, and asserting either way is wrong. The
		// server finishes work already running before it exits -- measured at fourteen seconds for a
		// call whose solution was still loading -- so the reply is written if stdout is still being
		// read and lost if it is not. Awaited rather than abandoned so the outcome is observed and
		// neither outcome fails: what this test claims is that the process ends and the worker goes
		// with it, both asserted above.
		//
		// The first version asserted that no answer arrives. It passed five runs out of six and then
		// failed on "No exception was thrown", which is the assertion being wrong rather than the
		// behaviour changing.
		try
		{
			using var answered = await call;
			Assert.True(
				answered.RootElement.TryGetProperty("result", out _) || answered.RootElement.TryGetProperty("error", out _),
				"a reply that arrives at all has to be a JSON-RPC result or error");
		}
		catch (Exception exception) when (exception is not TrueException)
		{
			// The other legitimate outcome: the stream went before the answer did.
		}
	}

	/// <summary>
	/// A worker that dies is noticed when it dies, without anyone calling it.
	/// </summary>
	/// <remarks>
	/// Nothing observed exit: the exit reason flipped inside the send path, so a worker that had
	/// crashed while idle went on describing itself as alive until somebody asked it something. The
	/// tray polls exactly this description every couple of seconds, so it showed a dead worker as
	/// loaded for as long as nobody used it -- which is the situation someone opens that window in.
	/// <para>
	/// Waiting for the workspace to go quiet first is what makes this discriminate, and the first
	/// draft did not: killing a worker while its load was still finishing meant the heap refresh
	/// that follows every call met the dead process and flipped the reason anyway, so the test passed
	/// against the un-fixed broker. Nothing polls a worker on its own once the load is done, so a
	/// reason that changes after that changed because the exit was observed.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_crashed_worker_is_noticed_without_being_called()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), cancellationToken);

		Assert.True(worker.IsAlive, $"the worker should be alive; exit reason was '{worker.ExitReason}'");
		Assert.NotNull(worker.ProcessId);

		// The load, and then the heap read that follows it. Both are calls, and a call notices a dead
		// worker by itself -- which is the behaviour this test has to run after rather than alongside.
		await WaitForLoadAsync(worker, cancellationToken);
		await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

		Assert.True(worker.IsAlive, "the worker should still be alive with nothing having been asked of it");

		using (var process = Process.GetProcessById(worker.ProcessId!.Value))
		{
			process.Kill(entireProcessTree: true);
		}

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
		while (worker.IsAlive && DateTime.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		Assert.False(worker.IsAlive, "a worker killed from outside should be reported dead without being called");
		Assert.Equal(WorkerExitReason.Crashed, worker.ExitReason);

		// And the description the tray reads agrees, which is the thing that was wrong.
		var described = Assert.Single(manager.Describe());

		Assert.False(described.Alive, "the description a tray polls should agree that the worker is gone");
		Assert.Equal(WorkspaceState.Faulted, described.State);
	}

	/// <summary>
	/// Waits for a worker's initial load to finish, which is what makes it safe to say that nothing
	/// is about to call it.
	/// </summary>
	private static async Task WaitForLoadAsync(WorkspaceWorker worker, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

		while (DateTime.UtcNow < deadline)
		{
			if (worker.LoadDuration is not null || !worker.IsAlive) return;

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		throw new InvalidOperationException($"The worker for {worker.SolutionPath} never finished loading.");
	}

	/// <summary>
	/// The retry after a worker dies replaces the dead instance rather than closing whatever is
	/// registered for the path.
	/// </summary>
	/// <remarks>
	/// The retry called the restart path, which removes and closes whatever is registered whether or
	/// not it is the instance that just died. Two callers on one dead worker and the second closes
	/// the replacement the first is already loading a solution into, mid-load. Replacing only a dead
	/// instance is what <c>GetOrStart</c> already does.
	/// <para>
	/// This is a characterisation test and it passes against the un-fixed broker, which was checked
	/// rather than assumed. With one caller the two paths are indistinguishable: both end with one
	/// live replacement serving the call. The difference needs two callers arriving on the same dead
	/// worker inside the same window, which is not something a test can arrange reliably -- one that
	/// tried would be asserting a timing and would fail for reasons that are not this bug. What it
	/// does hold is the outcome nobody should be able to break quietly: one worker registered, not a
	/// closed one beside a live one, and the retried call answered.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_call_retried_after_a_worker_dies_is_served_by_one_replacement()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var hints = WorkspaceHints.From(fixture.SolutionPath);
		var original = await manager.GetOrStartAsync(hints, cancellationToken);

		using (var process = Process.GetProcessById(original.ProcessId!.Value))
		{
			process.Kill(entireProcessTree: true);
		}

		var status = await manager.CallAsync<WorkspaceStatusReport>(
			hints,
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?>(),
			retryIfWorkerDied: true,
			cancellationToken);

		Assert.Equal(WorkspaceState.Loaded, status.State);
		Assert.NotEmpty(status.Projects);

		var replacement = Assert.Single(manager.Workers);

		Assert.NotEqual(original.ProcessId, replacement.ProcessId);
		Assert.Equal(fixture.SolutionPath, replacement.SolutionPath);
		Assert.True(replacement.IsAlive, $"the replacement should be alive; exit reason was '{replacement.ExitReason}'");
	}

	/// <summary>The worker this server started, waited for rather than assumed to exist already.</summary>
	private static async Task<int> WaitForNewWorkerAsync(IReadOnlyCollection<int> before, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);

		while (DateTime.UtcNow < deadline)
		{
			var started = WorkerProcessIds().Except(before).ToList();
			if (started.Count > 0) return started[0];

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		throw new InvalidOperationException("The server never started a worker.");
	}

	/// <summary>
	/// Every worker on the machine, because a test cannot ask a server it is deliberately not
	/// talking to. Compared as a difference rather than a count, so another session's workers -- a
	/// tray's, most often -- are not mistaken for this one's.
	/// </summary>
	private static IReadOnlyList<int> WorkerProcessIds()
	{
		var running = Process.GetProcessesByName("RoseMcp.Worker");
		var ids = running.Select(process => process.Id).ToList();
		foreach (var process in running) process.Dispose();

		return ids;
	}

	private static WorkspaceManager CreateManager(string? defaultRoot = null) => new(
		Options.Create(new BrokerOptions
		{
			// Somewhere with no solution, unless a test is specifically exercising discovery. It used
			// to say %TEMP%, which is not that -- a developer's temp collects stray .csproj files, and
			// resolution walks up -- and it went unnoticed because a bare call was answered from the
			// open worker before the root was ever consulted. Now that a bare call really does resolve
			// from here, it has to mean what it says.
			DefaultWorkspaceRoot = defaultRoot ?? NowhereDirectory.Path(),
		}),
		NullLoggerFactory.Instance,
		NullLogger<WorkspaceManager>.Instance);

	/// <summary>
	/// A malformed argument names the argument, over the wire and through the whole stack rather than
	/// in a unit test of the sentence.
	/// <para>
	/// The binder's own account is "The JSON value could not be converted to System.String[]. Path: $",
	/// which is accurate and unusable: it names a CLR type the caller never wrote, points at the root
	/// of the document rather than the property, and does not say which of the tool's several
	/// string-ish arguments was the array. Every other refusal on this surface says what was wrong with
	/// what was sent and what to send instead.
	/// </para>
	/// <para>
	/// Deliberately failed rather than read hop by hop, because what this claims is compositional: the
	/// tool's schema has to reach the filter, the filter has to run before the SDK's own wrapper, and
	/// the message has to survive the trip back. Reading each of those cannot detect the one that is
	/// missing.
	/// </para>
	/// </summary>
	[Test]
	public async Task Names_the_malformed_argument_rather_than_a_clr_type()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		using var server = RoseServerProcess.Start("--port", RoseServerProcess.FreePort().ToString());
		await server.InitializeAsync(cancellationToken);

		// arguments takes a list of strings; this sends the one element bare, which is the mistake.
		using var reply = await server.CallToolAsync(
			ToolNames.ChangeSignature,
			$$"""
			{"workspace":{{JsonSerializer.Serialize(fixture.SolutionPath)}},"symbol":"Simple.Greeter.Greet","parameters":"string name","arguments":"name=\"x\""}
			""",
			cancellationToken);

		var text = reply.RootElement.GetRawText();

		Assert.Contains("arguments takes a list of strings", text, StringComparison.Ordinal);
		Assert.Contains("a string was sent", text, StringComparison.Ordinal);
		Assert.DoesNotContain("System.String[]", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A change that reaches another solution says so, in the result the caller actually reads.
	/// <para>
	/// Roslyn renames within one <c>Solution</c> and writes to disk, where a sibling solution over the
	/// same projects picks the new text up while still calling the old name from projects the renaming
	/// solution never had. That sibling is not stale, it is broken -- and the only thing standing
	/// between a caller and finding that out at the next build is a sentence in <c>Notices</c>.
	/// </para>
	/// <para>
	/// End to end through a real worker rather than against <c>SolutionResolver.SiblingsSharing</c>,
	/// which is what was already covered. Whether the overlap is found and whether the sentence reaches
	/// the caller are two claims, and the second is the one no test made: attribution is added in
	/// <c>WorkspaceManager</c>, after the worker has answered and to a result the worker knows nothing
	/// about, so nothing inside the worker could fail if the wiring went.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_change_that_another_solution_also_compiles_says_so()
	{
		using var fixture = FixtureSolution.Copy("Siblings", "Repo.slnx");
		await using var manager = CreateManager();
		var tools = new RoseMcp.Broker.Tools.BrokerAnalysisTools(manager);
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var renamed = await tools.RenameSymbolAsync(
			new Progress<ProgressNotificationValue>(),
			symbol: "Shared.Widget.Describe",
			newName: "Explain",
			filePath: null,
			workspace: fixture.SolutionPath,
			apply: true,
			cancellationToken: cancellationToken);

		Assert.True(renamed.Applied);

		Assert.Contains(
			renamed.Notices,
			notice => notice.Contains("Repo.Installer.slnx", StringComparison.Ordinal)
				&& notice.Contains("also compiles", StringComparison.Ordinal));
	}
}
