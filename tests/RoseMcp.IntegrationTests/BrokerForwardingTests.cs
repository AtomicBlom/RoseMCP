using System.Text.Json;

using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Broker.Tools;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;

using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a call gains by passing through the broker: it reaches the worker's tool and comes back
/// carrying who answered it. Every result names its workspace and that attribution is added once, in
/// WorkspaceManager, so a tool added later cannot forget it -- which is what these check, against
/// several tools rather than one.
/// <para>
/// Errors are the other half. A failing tool says what went wrong and where, and a malformed argument
/// is named as the argument rather than reported as a CLR type, because the conversion happens at the
/// MCP boundary where the exception still means something.
/// </para>
/// </summary>
public sealed class BrokerForwardingTests
{
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

		var error = await Should.ThrowAsync<Exception>(() => manager.CallAsync<SymbolInfoResult>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["filePath"] = elsewhere, ["line"] = 1, ["column"] = 1 },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken));

		error.Message.ShouldContain(elsewhere, Case.Insensitive);
		error.Message.ShouldContain(fixture.SolutionPath, Case.Insensitive);
	}

	/// <summary>
	/// A refusal from a workspace the path is not in gains the solution it is in. The worker can only
	/// say the path is in none of its own projects, which is true and sends the caller nowhere; the
	/// broker chose that worker, so the broker is what says which solution would take the path.
	/// </summary>
	[Test]
	public async Task A_refusal_from_another_workspace_names_the_solution_the_path_is_in()
	{
		using var simple = FixtureSolution.Copy("Simple", "Simple.sln");
		using var members = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var file = Path.Combine(Path.GetDirectoryName(members.SolutionPath)!, "Library", "Made", "Placed.cs");

		var error = await Should.ThrowAsync<InvalidOperationException>(() => manager.CallAsync<AddFileResult>(
			WorkspaceHints.ForNewFile(RootedPath.Absolute(simple.SolutionPath), RootedPath.Absolute(file)),
			ToolNames.AddFile,
			new Dictionary<string, object?> { ["filePath"] = file, ["code"] = "public sealed class Placed;" },
			retryIfWorkerDied: false,
			TestContext.Current!.Execution.CancellationToken)).OfExactType();

		error.Message.ShouldContain("not inside the directory of any project in Simple.sln", Case.Sensitive);
		error.Message.ShouldContain($"is inside a project of {members.SolutionPath}", Case.Insensitive);
		error.Message.ShouldContain("workspace argument", Case.Sensitive);
		File.Exists(file).ShouldBeFalse("a refusal writes nothing");
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

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

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

		replaced.Applied.ShouldBeTrue();
		replaced.Verified.ShouldBeTrue();
		replaced.IntroducedDiagnostics.ShouldBeEmpty();

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

		body.Applied.ShouldBeTrue();

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

		added.Applied.ShouldBeTrue();
		added.Members.ShouldBe(["Doubled"]);

		// And the file on disk carries all three, in the repository's own formatting.
		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Greeter.cs"), TestContext.Current!.Execution.CancellationToken);

		text.ShouldContain("\tpublic string Greet(string name) => $\"{_prefix}! {name}\";\r\n", Case.Sensitive);
		text.ShouldContain("\tpublic int Doubled => Count * 2;\r\n", Case.Sensitive);

		// Statements, so a block: the shape follows what was supplied rather than what was there.
		text.ShouldContain(
			"\tprivate static string Shout(string text)\r\n\t{\r\n\t\treturn text.ToLowerInvariant();\r\n\t}\r\n", Case.Sensitive);
	}

	/// <summary>Naming a symbol rather than a position has to survive the same trip.</summary>
	[Test]
	public async Task Describes_a_named_symbol_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var info = await manager.CallAsync<SymbolInfoResult>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["symbol"] = "Library.Greeter.PrefixLength" },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("PrefixLength");

		var span = info.DeclarationSpans.ShouldHaveSingleItem();

		span.LineCount.ShouldBe(2);
		span.FilePath.ShouldEndWith("Greeter.cs", Case.Insensitive);
	}

	/// <summary>
	/// A metadata type's members, narrowed and capped, through the broker's own tool method rather than
	/// straight to the worker: an argument the broker declares and leaves out of what it forwards is
	/// bound at its default on the other side, which here would list every member and read as a filter
	/// ignored.
	/// </summary>
	[Test]
	public async Task Lists_a_metadata_types_members_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();
		var tools = new BrokerAnalysisTools(manager, CreatePaths());

		var info = await tools.SymbolInfoAsync(
			new Progress<ProgressNotificationValue>(),
			symbol: "System.Text.StringBuilder",
			members: "Append",
			maxMembers: 3,
			workspace: fixture.SolutionPath,
			cancellationToken: TestContext.Current!.Execution.CancellationToken);

		var members = info.Members.ShouldNotBeNull();

		members.Count.ShouldBe(3);
		members.ShouldAllBe(member => member.Name.Contains("Append", StringComparison.Ordinal));
		info.Truncated.ShouldBeTrue();
		info.TotalMembers.ShouldNotBeNull().ShouldBeGreaterThan(3);
	}

	/// <summary>
	/// A reference's file comes back relative to the calling session's directory, and handed straight
	/// back as a position it names the same file: the caller quotes back what it was given and the call
	/// resolves, with no workspace key or root sent beside it.
	/// </summary>
	[Test]
	public async Task A_references_relative_path_resolves_when_it_is_sent_back()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();
		var origin = Path.GetDirectoryName(fixture.SolutionPath)!;
		var tools = new BrokerAnalysisTools(manager, CreatePaths(origin));
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var references = await tools.FindReferencesAsync(
			new Progress<ProgressNotificationValue>(),
			symbol: "Library.Greeter.Greet(string)",
			workspace: fixture.SolutionPath,
			cancellationToken: cancellationToken);

		references.RelativeTo.ShouldBe(origin);

		var file = references.Files.First();
		Path.IsPathRooted(file.FilePath).ShouldBeFalse($"'{file.FilePath}' should be relative to the session's directory");

		var site = file.References.First();
		var described = await tools.SymbolInfoAsync(
			new Progress<ProgressNotificationValue>(),
			filePath: file.FilePath,
			line: site.Line,
			column: site.Column,
			cancellationToken: cancellationToken);

		described.Name.ShouldBe("Greet");
		described.Kind.ShouldBe("Method");
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
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.ChangeSignature,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Notifier.Notify(string)",
				["parameters"] = "string message, bool urgent",
				["arguments"] = new[] { "urgent=false" },
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		// The interface, the base and the override, plus the two call sites in the forwarder.
		result.UpdatedDeclarations.Count.ShouldBe(3);
		result.UpdatedCallSites.Count.ShouldBe(3);

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Layers.cs"), TestContext.Current!.Execution.CancellationToken);

		text.ShouldContain("public override string Notify(string text, bool urgent)", Case.Sensitive);
		text.ShouldContain("notifier.Notify(message, false)", Case.Sensitive);
	}

	/// <summary>
	/// The imports a new parameter needs, over the wire: an argument the worker does not bind by this
	/// name is one a caller passes and the tool never sees.
	/// </summary>
	[Test]
	public async Task Imports_what_a_changed_signature_needs_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var result = await manager.CallAsync<SignatureChangeResult>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.ChangeSignature,
			new Dictionary<string, object?>
			{
				["symbol"] = "Library.Greeter.Greet(string)",
				["parameters"] = "string name, StringBuilder? into = null",
				["usings"] = new[] { "System.Text" },
			},
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldContain("Imported System.Text into Greeter.cs.");

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Greeter.cs"), TestContext.Current!.Execution.CancellationToken);

		text.ShouldStartWith("using System.Text;", Case.Sensitive);
	}

	/// <summary>Build freshness over the wire, so its one argument cannot drift either.</summary>
	[Test]
	public async Task Reports_build_freshness_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var report = await manager.CallAsync<BuildFreshnessReport>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.BuildFreshness,
			new Dictionary<string, object?> { ["project"] = "Core" },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		var project = report.Projects.ShouldHaveSingleItem();

		project.Project.ShouldBe("Core");
		project.Stale.ShouldBeTrue("a fresh copy has no build output at all");
		report.StaleCount.ShouldBe(1);
	}

	/// <summary>
	/// Both halves of importing, over the wire: the argument on a write tool, and the tool of its own.
	/// </summary>
	[Test]
	public async Task Imports_a_namespace_through_the_broker()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var manager = CreateManager();

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

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

		written.Applied.ShouldBeTrue();
		written.IntroducedDiagnostics.ShouldBeEmpty();

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

		again.Added.ShouldBeEmpty();
		again.Applied.ShouldBeFalse("the second call finds the import already there");
		again.AlreadyInScope.ShouldContain(reason => reason.Contains("already imported here", StringComparison.Ordinal));
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
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.SearchSymbols,
			new Dictionary<string, object?> { ["query"] = "Calculator" },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken);

		result.Workspace.ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
		result.WorkspaceKey.ShouldStartWith("Simple-", Case.Sensitive);
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

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), TestContext.Current!.Execution.CancellationToken);
		var status = await manager.StatusOfAsync(worker, TestContext.Current!.Execution.CancellationToken);

		status.Workspace.ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
		status.WorkspaceKey.ShouldBe(worker.Key);
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

		var before = await manager.GetOrStartAsync(WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), TestContext.Current!.Execution.CancellationToken);
		var key = before.Key;

		var after = await manager.RestartAsync(WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), TestContext.Current!.Execution.CancellationToken);

		after.ProcessId.ShouldNotBe(before.ProcessId);
		after.Key.ShouldBe(key);
	}

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

		using var server = RoseServerProcess.Start();
		await server.InitializeAsync(cancellationToken);

		// arguments takes a list of strings; this sends the one element bare, which is the mistake.
		using var reply = await server.CallToolAsync(
			ToolNames.ChangeSignature,
			$$"""
			{"workspace":{{JsonSerializer.Serialize(fixture.SolutionPath)}},"symbol":"Simple.Greeter.Greet","parameters":"string name","arguments":"name=\"x\""}
			""",
			cancellationToken);

		var text = reply.RootElement.GetRawText();

		text.ShouldContain("arguments takes a list of strings", Case.Sensitive);
		text.ShouldContain("a string was sent", Case.Sensitive);
		text.ShouldNotContain("System.String[]", Case.Sensitive);
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
		var tools = new RoseMcp.Broker.Tools.BrokerAnalysisTools(manager, CreatePaths());
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var renamed = await tools.RenameSymbolAsync(
			new Progress<ProgressNotificationValue>(),
			symbol: "Shared.Widget.Describe",
			newName: "Explain",
			filePath: null,
			workspace: fixture.SolutionPath,
			apply: true,
			cancellationToken: cancellationToken);

		renamed.Applied.ShouldBeTrue();

		renamed.Notices.ShouldContain(
			notice => notice.Contains("Repo.Installer.slnx", StringComparison.Ordinal)
				&& notice.Contains("also compiles", StringComparison.Ordinal));
	}

	/// <summary>
	/// A structural rewrite into files another solution also compiles says so too, counting every one of
	/// them. The rewrite is the one write whose changed files the broker cuts before the caller sees them,
	/// and the sibling notice is worked out from that same list, so this holds the order: with more shared
	/// files than the cut keeps, the notice still counts them all.
	/// </summary>
	[Test]
	public async Task A_rewrite_that_another_solution_also_compiles_says_so_past_the_changed_file_cap()
	{
		using var fixture = FixtureSolution.Copy("Siblings", "Repo.slnx");
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var callers = PatternRewriteForCaller.ChangedFileRows + 1;

		await File.WriteAllTextAsync(
			fixture.Path("Siblings", "Shared", "Text.cs"),
			"namespace Shared;\r\n\r\npublic static class Text\r\n{\r\n\tpublic static string Shout(string value) => value.ToUpperInvariant();\r\n}\r\n",
			cancellationToken);

		for (var index = 0; index < callers; index++)
		{
			await File.WriteAllTextAsync(
				fixture.Path("Siblings", "Shared", $"Caller{index:D2}.cs"),
				$"namespace Shared;\r\n\r\npublic static class Caller{index:D2}\r\n{{\r\n\tpublic static string Greet(string name) => Text.Shout(name);\r\n}}\r\n",
				cancellationToken);
		}

		await using var manager = CreateManager();
		var tools = new BrokerAnalysisTools(manager, CreatePaths());

		var rewritten = await tools.ReplacePatternAsync(
			new Progress<ProgressNotificationValue>(),
			rules: [new PatternRule { Find = "Shared.Text.Shout($v$)", Replace = "$v$.ToUpperInvariant()" }],
			apply: true,
			workspace: fixture.SolutionPath,
			cancellationToken: cancellationToken);

		rewritten.Applied.ShouldBeTrue();
		rewritten.FilesChanged.ShouldBe(callers);
		rewritten.ChangedFiles.Count.ShouldBe(PatternRewriteForCaller.ChangedFileRows);

		rewritten.Notices.ShouldContain(
			notice => notice.StartsWith($"Repo.Installer.slnx also compiles {callers} of the file(s) this changed", StringComparison.Ordinal),
			string.Join(Environment.NewLine, rewritten.Notices));
		rewritten.Notices.ShouldContain(notice => notice.StartsWith("changedFiles names ", StringComparison.Ordinal));
	}

	/// <summary>
	/// The wrong-checkout write, end to end, with the argument shape that produced it: a relative
	/// filePath that names a real file in every checkout of the repository. It has to land in the one
	/// the session is calling from, and the other one has to be untouched -- which is the half no
	/// caller could check, since their own git status is clean either way.
	/// <para>
	/// The session is standing in a subdirectory rather than at the solution root, which is what makes
	/// this fail on a hop that forwards the path as the caller wrote it: the worker measures a relative
	/// path from its solution's root, and the two bases are only ever the same by coincidence.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_relative_path_is_written_in_the_checkout_the_call_came_from()
	{
		using var elsewhere = FixtureSolution.Copy("Simple", "Simple.sln");
		using var here = FixtureSolution.Copy("Simple", "Simple.sln");

		// The broker is running in one checkout; the session is calling from the other.
		var brokerSitsIn = Path.GetDirectoryName(elsewhere.SolutionPath)!;

		await using var manager = CreateManager(brokerSitsIn);
		var tools = new RoseMcp.Broker.Tools.BrokerAnalysisTools(manager, CreatePaths(brokerSitsIn));

		using var origin = CallOrigin.Use(here.Path("Simple", "Core"));

		var added = await tools.AddUsingAsync(
			new Progress<ProgressNotificationValue>(),
			filePath: "Calculator.cs",
			namespaces: ["System.Text"],
			cancellationToken: TestContext.Current!.Execution.CancellationToken);

		added.Applied.ShouldBeTrue();
		added.Workspace.ShouldBe(here.SolutionPath, StringCompareShould.IgnoreCase);

		(await File.ReadAllTextAsync(here.Path("Simple", "Core", "Calculator.cs"))).ShouldContain(
			"System.Text", Case.Sensitive);

		(await File.ReadAllTextAsync(elsewhere.Path("Simple", "Core", "Calculator.cs"))).ShouldNotContain(
			"System.Text", Case.Sensitive);
	}

	/// <summary>
	/// The other half of the same rule: nothing but the broker knows what a relative path is measured
	/// from, so a worker that resolves one against its own working directory writes to a plausible
	/// file and reports success. Refusing is what turns a mis-routed call into a sentence.
	/// </summary>
	[Test]
	public async Task A_worker_refuses_a_relative_path_rather_than_resolving_one()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var error = await Should.ThrowAsync<Exception>(() => manager.CallAsync<SymbolInfoResult>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.SymbolInfo,
			new Dictionary<string, object?> { ["filePath"] = Path.Combine("Core", "Calculator.cs") },
			retryIfWorkerDied: true,
			TestContext.Current!.Execution.CancellationToken));

		error.Message.ShouldContain("filePath", Case.Sensitive);
		error.Message.ShouldContain("absolute", Case.Sensitive);
	}

	/// <summary>
	/// The list-shaped path argument, which is the one tool where a caller sends several. Nothing
	/// else drives it through the broker, and a list resolved one way and a single path another is
	/// exactly the drift `filePaths` is spelled once to avoid.
	/// </summary>
	[Test]
	public async Task A_list_of_relative_paths_is_made_absolute_like_a_single_one()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var solutionDirectory = Path.GetDirectoryName(fixture.SolutionPath)!;

		await using var manager = CreateManager();
		var tools = new RoseMcp.Broker.Tools.BrokerAnalysisTools(manager, CreatePaths());

		using var origin = CallOrigin.Use(solutionDirectory);

		var formatted = await tools.FormatAsync(
			new Progress<ProgressNotificationValue>(),
			filePaths: [Path.Combine("Core", "Calculator.cs")],
			apply: false,
			cancellationToken: TestContext.Current!.Execution.CancellationToken);

		formatted.FilesInspected.ShouldBe(1);
		formatted.Workspace.ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}
}
