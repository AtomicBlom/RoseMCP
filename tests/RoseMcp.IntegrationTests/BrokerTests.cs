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

		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

		var restarted = await manager.RestartAsync(
			WorkspaceHints.From(fixture.SolutionPath),
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

		var first = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);
		var status = await first.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, new Dictionary<string, object?>(), TestContext.Current.CancellationToken);

		Assert.Equal(WorkspaceState.Loaded, status.State);

		// Resolve from a source file this time; it must land on the same worker.
		var second = await manager.GetOrStartAsync(
			WorkspaceHints.From(fixture.Path("Simple", "Core", "Calculator.cs")),
			TestContext.Current.CancellationToken);

		Assert.Same(first, second);
		Assert.Single(manager.Workers);
	}

	[Fact]
	public async Task Restart_replaces_the_worker_process()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var before = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);
		var after = await manager.RestartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

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

		await manager.GetOrStartAsync(WorkspaceHints.From(simple.SolutionPath), TestContext.Current.CancellationToken);
		await manager.GetOrStartAsync(WorkspaceHints.From(generator.SolutionPath), TestContext.Current.CancellationToken);

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
			() => manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current.CancellationToken));

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
	[Fact]
	public async Task Does_not_answer_from_a_workspace_another_session_left_open()
	{
		using var open = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		using var mine = FixtureSolution.Copy("Simple", "Simple.sln");

		// Rooted where this session actually is, with the other solution already loaded.
		await using var manager = CreateManager(Path.GetDirectoryName(mine.SolutionPath)!);

		var theirs = await manager.GetOrStartAsync(
			WorkspaceHints.From(open.SolutionPath), TestContext.Current.CancellationToken);

		var bare = await manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current.CancellationToken);

		Assert.NotSame(theirs, bare);
		Assert.Equal(mine.SolutionPath, bare.SolutionPath, ignoreCase: true);
	}

	/// <summary>
	/// And with nothing to resolve from, a loaded workspace is still not an answer -- it is only a
	/// suggestion in the failure.
	/// </summary>
	[Fact]
	public async Task Refuses_rather_than_borrowing_the_only_open_workspace()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		await manager.GetOrStartAsync(
			WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

		var error = await Assert.ThrowsAsync<McpException>(
			() => manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current.CancellationToken));

		Assert.Contains(fixture.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Closing_stops_the_worker_and_forgets_it()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

		Assert.True(await manager.CloseAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken));
		Assert.Empty(manager.Workers);
		Assert.False(worker.IsAlive);

		// Closing something that is not open is a no-op, not an error.
		Assert.False(await manager.CloseAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Refuses_a_solution_that_is_not_there()
	{
		await using var manager = CreateManager();

		var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}", "Nope.sln");

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => manager.GetOrStartAsync(WorkspaceHints.From(missing), TestContext.Current.CancellationToken));

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

		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

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
		var worker = await manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current.CancellationToken);

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
			() => manager.GetOrStartAsync(WorkspaceHints.None, TestContext.Current.CancellationToken));

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

		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

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
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["filePath"] = elsewhere, ["line"] = 1, ["column"] = 1 },
			retryIfWorkerDied: true,
			TestContext.Current.CancellationToken));

		Assert.Contains(elsewhere, error.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(fixture.SolutionPath, error.Message, StringComparison.OrdinalIgnoreCase);
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
	[Fact]
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
			TestContext.Current.CancellationToken);

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
			TestContext.Current.CancellationToken);

		Assert.True(body.Applied);

		var added = await manager.CallAsync<MemberEditResult>(
			hints,
			ToolNames.AddMember,
			new Dictionary<string, object?>
			{
				["type"] = "Library.Greeter",
				["code"] = "public int Doubled => Count * 2;",
				["after"] = "Count",
			},
			retryIfWorkerDied: true,
			TestContext.Current.CancellationToken);

		Assert.True(added.Applied);
		Assert.Equal(["Doubled"], added.Members);

		// And the file on disk carries all three, in the repository's own formatting.
		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Greeter.cs"), TestContext.Current.CancellationToken);

		Assert.Contains("\tpublic string Greet(string name) => $\"{_prefix}! {name}\";\r\n", text, StringComparison.Ordinal);
		Assert.Contains("\tpublic int Doubled => Count * 2;\r\n", text, StringComparison.Ordinal);

		// Statements, so a block: the shape follows what was supplied rather than what was there.
		Assert.Contains(
			"\tprivate static string Shout(string text)\r\n\t{\r\n\t\treturn text.ToLowerInvariant();\r\n\t}\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>Naming a symbol rather than a position has to survive the same trip.</summary>
	[Fact]
	public async Task Describes_a_named_symbol_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var info = await manager.CallAsync<SymbolInfoResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["symbol"] = "Library.Greeter.PrefixLength" },
			retryIfWorkerDied: true,
			TestContext.Current.CancellationToken);

		Assert.Equal("PrefixLength", info.Name);

		var span = Assert.Single(info.DeclarationSpans);

		Assert.Equal(2, span.LineCount);
		Assert.EndsWith("Greeter.cs", span.FilePath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Changing a signature over the wire, which is where an argument the broker spells differently
	/// would show up -- and this one has the most arguments of any tool here.
	/// </summary>
	[Fact]
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
			TestContext.Current.CancellationToken);

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);

		// The interface, the base and the override, plus the two call sites in the forwarder.
		Assert.Equal(3, result.UpdatedDeclarations.Count);
		Assert.Equal(3, result.UpdatedCallSites.Count);

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Layers.cs"), TestContext.Current.CancellationToken);

		Assert.Contains("public override string Notify(string text, bool urgent)", text, StringComparison.Ordinal);
		Assert.Contains("notifier.Notify(message, false)", text, StringComparison.Ordinal);
	}

	/// <summary>Build freshness over the wire, so its one argument cannot drift either.</summary>
	[Fact]
	public async Task Reports_build_freshness_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var report = await manager.CallAsync<BuildFreshnessReport>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.BuildFreshness,
			new Dictionary<string, object?> { ["project"] = "Core" },
			retryIfWorkerDied: true,
			TestContext.Current.CancellationToken);

		var project = Assert.Single(report.Projects);

		Assert.Equal("Core", project.Project);
		Assert.True(project.Stale, "a fresh copy has no build output at all");
		Assert.Equal(1, report.StaleCount);
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
			WorkspaceHints.From(fixture.SolutionPath),
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

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);
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

		var before = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);
		var key = before.Key;

		var after = await manager.RestartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

		Assert.NotEqual(before.ProcessId, after.ProcessId);
		Assert.Equal(key, after.Key);
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
}
