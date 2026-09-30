using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A structural rewrite over a real solution, with the real assertion libraries: what each rule
/// takes, what nothing takes, and what is refused.
/// <para>
/// The fixture references xunit.v3.assert and Shouldly at the versions the repository's own suites
/// move between, because what a rule means is decided by the metadata it binds to -- the overload
/// shapes, the parameter names a rule can pin with, the defaults it has to see left alone -- and a
/// stub can only say what its author believed about those.
/// </para>
/// </summary>
public sealed class ReplacePatternTests
{
	/// <summary>
	/// The catalog shape the migration uses, specific before general: each rule's count, the rule that
	/// lost a site to an earlier one, and the calls into Assert that no rule is written for.
	/// </summary>
	[Test]
	public async Task A_preview_counts_what_each_rule_takes_and_lists_what_none_does()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await PreviewAsync(
			session,
			Rule("Assert.Equal($e:string$, $a:string$, ignoreCase: true)", "$a$.ShouldBe($e$, StringCompareShould.IgnoreCase)"),
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"),
			Rule("Assert.True(!$c$)", "$c$.ShouldBeFalse()"),
			Rule("Assert.True($c$)", "$c$.ShouldBeTrue()"),
			Rule("Assert.Contains($sub:string$, $s:string$, StringComparison.Ordinal)", "$s$.ShouldContain($sub$, Case.Sensitive)"),
			Rule("Assert.Contains($xs$, filter: $p$)", "$xs$.ShouldContain($p$)"),
			Rule("Assert.Contains($item$, $xs$)", "$xs$.ShouldContain($item$)"));

		result.Applied.ShouldBeFalse();
		// Rule 2 takes Equality's two plain calls and both calls in Skips. NoGlobal has no global alias
		// for Assert, so no rule binds there without Xunit in usings, and its call is not counted at all.
		result.Rules.Select(rule => rule.Matched).ShouldBe([1, 4, 1, 2, 1, 1, 2]);
		result.Rules[3].Outranked.ShouldBe(1);
		result.SitesMatched.ShouldBe(12);

		// The precision Equal, the ignore-case Contains, DoesNotContain, Single and All: five calls into
		// Assert, each named by the overload it calls so the gap in the catalog is listed, not guessed.
		result.SitesUnmatched.ShouldBe(5);
		result.Unmatched.ShouldContain(group => group.Method.Contains("Equal(double, double, int)", StringComparison.Ordinal));
		result.Unmatched.ShouldContain(group => group.Method.Contains(".Single", StringComparison.Ordinal));
		result.Notices.ShouldContain("Preview only; nothing was written to disk.");
	}

	/// <summary>
	/// The catalog applied: each file written in the shape its rule says, the call no rule is written for
	/// left alone, and everything that changed compiling afterwards.
	/// </summary>
	[Test]
	public async Task Applies_the_catalog_and_everything_it_changed_compiles()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await RunAsync(session, new ReplacePatternRequest
		{
			Rules = Catalog,
			Usings = ["Shouldly"],
			FilePaths = [fixture.Path("Assertions", "Tests")],
		});

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.TotalErrorCount.ShouldBe(0);

		var equality = await ReadAsync(fixture, "Tests", "Equality.cs");

		equality.ShouldContain("count.ShouldBe(1);", Case.Sensitive);
		equality.ShouldContain("count.ShouldBe(2);", Case.Sensitive);
		equality.ShouldContain("text.ShouldBe(\"a\", StringCompareShould.IgnoreCase);", Case.Sensitive);
		equality.ShouldContain("Assert.Equal(1.5, ratio, 3);", Case.Sensitive);
		equality.ShouldContain("(count > 0).ShouldBeTrue();", Case.Sensitive);
		equality.ShouldContain("(count > 1 && count < 5).ShouldBeFalse();", Case.Sensitive);

		var strings = await ReadAsync(fixture, "Tests", "Strings.cs");

		strings.ShouldContain("text.ShouldContain(\"a\", Case.Sensitive);", Case.Sensitive);
		strings.ShouldContain("text.ShouldContain(\"b\", Case.Insensitive);", Case.Sensitive);
		strings.ShouldContain("text.ShouldContain(\"c\", Case.Sensitive);", Case.Sensitive);
		strings.ShouldContain("text.ShouldNotContain('\\r');", Case.Sensitive);

		var collections = await ReadAsync(fixture, "Tests", "Collections.cs");

		collections.ShouldContain("items.ShouldContain(3);", Case.Sensitive);
		collections.ShouldContain("items.ShouldContain(item => item > 2);", Case.Sensitive);
		collections.ShouldContain("var only = items.ShouldHaveSingleItem();", Case.Sensitive);
		collections.ShouldContain("foreach (var item in items) { (item > 0).ShouldBeTrue(); }", Case.Sensitive);
	}

	/// <summary>
	/// A replacement Shouldly cannot bind -- an int compared with a long, which xunit converts and
	/// Shouldly's receiver does not -- is put back with the compiler's reason, byte for byte, and the call
	/// beside it is still written.
	/// </summary>
	[Test]
	public async Task Skips_a_replacement_that_does_not_compile_and_writes_its_neighbour()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await RunAsync(session, new ReplacePatternRequest
		{
			Rules = [Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)")],
			FilePaths = [fixture.Path("Assertions", "Tests", "Skips.cs")],
		});

		var skipped = result.Skipped.ShouldHaveSingleItem();
		var text = await ReadAsync(fixture, "Tests", "Skips.cs");

		((result.SitesRewritten, result.SitesSkipped)).ShouldBe((1, 1));
		skipped.DiagnosticId.ShouldStartWith("CS", Case.Sensitive);
		text.ShouldContain("\t\tAssert.Equal(1L, count);\r\n", Case.Sensitive);
		text.ShouldContain("\t\tcount.ShouldBe(2);\r\n", Case.Sensitive);
		result.TotalErrorCount.ShouldBe(0);
	}

	/// <summary>A namespace a replacement needs is imported where the file keeps its imports, and only there.</summary>
	[Test]
	public async Task Imports_what_a_replacement_needs_where_the_file_keeps_its_imports()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await RunAsync(session, new ReplacePatternRequest
		{
			Rules = [Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)")],
			// Xunit as well, because this project has no global alias for Assert: a find is compiled on
			// its own, where only the project's global usings and these reach it.
			Usings = ["Shouldly", "Xunit"],
			FilePaths = [fixture.Path("Assertions", "NoGlobal")],
		});

		var text = await ReadAsync(fixture, "NoGlobal", "Imports.cs");

		result.TotalErrorCount.ShouldBe(0);
		text.ShouldStartWith("using Shouldly;\r\nusing Xunit;\r\n", Case.Sensitive);
		text.ShouldContain("count.ShouldBe(1);", Case.Sensitive);
	}

	/// <summary>A preview builds and checks everything and writes nothing.</summary>
	[Test]
	public async Task A_preview_writes_nothing()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var before = await ReadAsync(fixture, "Tests", "Equality.cs");

		var result = await PreviewAsync(session, [fixture.Path("Assertions", "Tests", "Equality.cs")], Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		result.Applied.ShouldBeFalse();
		result.Diff.ShouldContain("count.ShouldBe(1);", Case.Sensitive);
		(await ReadAsync(fixture, "Tests", "Equality.cs")).ShouldBe(before);
	}

	/// <summary>A catalog for the fixture's shapes, specific before general.</summary>
	private static readonly PatternRule[] Catalog =
	[
		Rule("Assert.Equal($e:string$, $a:string$, ignoreCase: true)", "$a$.ShouldBe($e$, StringCompareShould.IgnoreCase)"),
		Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"),
		Rule("Assert.True(!$c$)", "$c$.ShouldBeFalse()"),
		Rule("Assert.True($c$)", "$c$.ShouldBeTrue()"),
		Rule("Assert.Contains($sub:string$, $s:string$, StringComparison.Ordinal)", "$s$.ShouldContain($sub$, Case.Sensitive)"),
		Rule("Assert.Contains($sub:string$, $s:string$, StringComparison.OrdinalIgnoreCase)", "$s$.ShouldContain($sub$, Case.Insensitive)"),
		Rule("Assert.Contains($sub:string$, $s:string$)", "$s$.ShouldContain($sub$, Case.Sensitive)"),
		Rule("Assert.Contains($xs$, filter: $p$)", "$xs$.ShouldContain($p$)"),
		Rule("Assert.Contains($item$, $xs$)", "$xs$.ShouldContain($item$)"),
		Rule("Assert.DoesNotContain($item$, $xs$)", "$xs$.ShouldNotContain($item$)"),
		Rule("Assert.Single($xs$)", "$xs$.ShouldHaveSingleItem()"),
		Rule("Assert.All($xs$, $x:id$ => $body$);", "foreach (var $x$ in $xs$) { $body$; }"),
	];

	/// <summary>A file in the fixture copy, as it is on disk now.</summary>
	private static Task<string> ReadAsync(FixtureSolution fixture, string project, string file) =>
		File.ReadAllTextAsync(fixture.Path("Assertions", project, file), TestContext.Current!.Execution.CancellationToken);

	/// <summary>A catalog none of whose rules binds anywhere is the caller's mistake, and says what did not bind.</summary>
	[Test]
	public async Task Refuses_a_catalog_that_binds_nowhere()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(() => PreviewAsync(session, Rule("Nowhere.Check($a$)", "$a$"))).OfExactType();

		error.Message.ShouldStartWith("No rule binds in any project in scope", Case.Sensitive);
		error.Message.ShouldContain("Rule 1's find does not bind at `Nowhere.Check`", Case.Sensitive);
	}

	/// <summary>A scope that names no file is refused, since a rewrite of nothing would read as one that found nothing to do.</summary>
	[Test]
	public async Task Refuses_a_path_that_names_nothing()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => PreviewAsync(session, [fixture.Path("Assertions", "Nowhere")], Rule("Assert.True($c$)", "$c$.ShouldBeTrue()"))).OfExactType();

		error.Message.ShouldStartWith("No C# file in this solution is at or under", Case.Sensitive);
	}

	/// <summary>
	/// The rules are the first argument any tool takes as objects, so the trip from the broker to the
	/// worker is proved rather than assumed: they arrive, bind, and the result names the workspace.
	/// </summary>
	[Test]
	public async Task The_broker_forwards_rules_to_the_worker()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var manager = CreateManager();
		var tools = new RoseMcp.Broker.Tools.BrokerAnalysisTools(manager, CreatePaths());

		var result = await tools.ReplacePatternAsync(
			new Progress<ProgressNotificationValue>(),
			rules: [Rule("Assert.True($c$)", "$c$.ShouldBeTrue()")],
			apply: false,
			workspace: fixture.SolutionPath,
			cancellationToken: TestContext.Current!.Execution.CancellationToken);

		result.Rules[0].Matched.ShouldBe(3);
		result.Workspace.ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>A rule as the tool takes it.</summary>
	private static PatternRule Rule(string find, string replace) => new() { Find = find, Replace = replace };

	/// <summary>A preview of <paramref name="rules"/> over the whole fixture.</summary>
	private static Task<PatternRewriteResult> PreviewAsync(WorkspaceSession session, params PatternRule[] rules) =>
		PreviewAsync(session, [], rules);

	/// <summary>A preview of <paramref name="rules"/> over <paramref name="filePaths"/>.</summary>
	private static Task<PatternRewriteResult> PreviewAsync(WorkspaceSession session, string[] filePaths, params PatternRule[] rules) =>
		RunAsync(session, new ReplacePatternRequest { Rules = rules, FilePaths = filePaths, Apply = false });

	/// <summary>The service run over <paramref name="session"/> as the tool runs it.</summary>
	private static Task<PatternRewriteResult> RunAsync(WorkspaceSession session, ReplacePatternRequest request)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		return session.MutateAsync(
			(snapshot, token) => ReplacePatternService.ReplaceAsync(snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}
}
