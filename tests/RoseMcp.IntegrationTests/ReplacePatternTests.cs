using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoseMcp.Contracts;

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

		Assert.False(result.Applied);
		// Rule 2 takes Equality's two plain calls and both calls in Skips. NoGlobal has no global alias
		// for Assert, so no rule binds there without Xunit in usings, and its call is not counted at all.
		Assert.Equal([1, 4, 1, 2, 1, 1, 2], result.Rules.Select(rule => rule.Matched));
		Assert.Equal(1, result.Rules[3].Outranked);
		Assert.Equal(12, result.SitesMatched);

		// The precision Equal, the ignore-case Contains, DoesNotContain, Single and All: five calls into
		// Assert, each named by the overload it calls so the gap in the catalog is listed, not guessed.
		Assert.Equal(5, result.SitesUnmatched);
		Assert.Contains(result.Unmatched, group => group.Method.Contains("Equal(double, double, int)", StringComparison.Ordinal));
		Assert.Contains(result.Unmatched, group => group.Method.Contains(".Single", StringComparison.Ordinal));
		Assert.Contains("Preview only; nothing was written to disk.", result.Notices);
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

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Equal(0, result.TotalErrorCount);

		var equality = await ReadAsync(fixture, "Tests", "Equality.cs");

		Assert.Contains("count.ShouldBe(1);", equality, StringComparison.Ordinal);
		Assert.Contains("count.ShouldBe(2);", equality, StringComparison.Ordinal);
		Assert.Contains("text.ShouldBe(\"a\", StringCompareShould.IgnoreCase);", equality, StringComparison.Ordinal);
		Assert.Contains("Assert.Equal(1.5, ratio, 3);", equality, StringComparison.Ordinal);
		Assert.Contains("(count > 0).ShouldBeTrue();", equality, StringComparison.Ordinal);
		Assert.Contains("(count > 1 && count < 5).ShouldBeFalse();", equality, StringComparison.Ordinal);

		var strings = await ReadAsync(fixture, "Tests", "Strings.cs");

		Assert.Contains("text.ShouldContain(\"a\", Case.Sensitive);", strings, StringComparison.Ordinal);
		Assert.Contains("text.ShouldContain(\"b\", Case.Insensitive);", strings, StringComparison.Ordinal);
		Assert.Contains("text.ShouldContain(\"c\", Case.Sensitive);", strings, StringComparison.Ordinal);
		Assert.Contains("text.ShouldNotContain('\\r');", strings, StringComparison.Ordinal);

		var collections = await ReadAsync(fixture, "Tests", "Collections.cs");

		Assert.Contains("items.ShouldContain(3);", collections, StringComparison.Ordinal);
		Assert.Contains("items.ShouldContain(item => item > 2);", collections, StringComparison.Ordinal);
		Assert.Contains("var only = items.ShouldHaveSingleItem();", collections, StringComparison.Ordinal);
		Assert.Contains("foreach (var item in items) { (item > 0).ShouldBeTrue(); }", collections, StringComparison.Ordinal);
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

		var skipped = Assert.Single(result.Skipped);
		var text = await ReadAsync(fixture, "Tests", "Skips.cs");

		Assert.Equal((1, 1), (result.SitesRewritten, result.SitesSkipped));
		Assert.StartsWith("CS", skipped.DiagnosticId, StringComparison.Ordinal);
		Assert.Contains("\t\tAssert.Equal(1L, count);\r\n", text, StringComparison.Ordinal);
		Assert.Contains("\t\tcount.ShouldBe(2);\r\n", text, StringComparison.Ordinal);
		Assert.Equal(0, result.TotalErrorCount);
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

		Assert.Equal(0, result.TotalErrorCount);
		Assert.StartsWith("using Shouldly;\r\nusing Xunit;\r\n", text, StringComparison.Ordinal);
		Assert.Contains("count.ShouldBe(1);", text, StringComparison.Ordinal);
	}

	/// <summary>A preview builds and checks everything and writes nothing.</summary>
	[Test]
	public async Task A_preview_writes_nothing()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var before = await ReadAsync(fixture, "Tests", "Equality.cs");

		var result = await PreviewAsync(session, [fixture.Path("Assertions", "Tests", "Equality.cs")], Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		Assert.False(result.Applied);
		Assert.Contains("count.ShouldBe(1);", result.Diff, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Tests", "Equality.cs"));
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

		var error = await Assert.ThrowsAsync<ArgumentException>(() => PreviewAsync(session, Rule("Nowhere.Check($a$)", "$a$")));

		Assert.StartsWith("No rule binds in any project in scope", error.Message, StringComparison.Ordinal);
		Assert.Contains("Rule 1's find does not bind at `Nowhere.Check`", error.Message, StringComparison.Ordinal);
	}

	/// <summary>A scope that names no file is refused, since a rewrite of nothing would read as one that found nothing to do.</summary>
	[Test]
	public async Task Refuses_a_path_that_names_nothing()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => PreviewAsync(session, [fixture.Path("Assertions", "Nowhere")], Rule("Assert.True($c$)", "$c$.ShouldBeTrue()")));

		Assert.StartsWith("No C# file in this solution is at or under", error.Message, StringComparison.Ordinal);
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

		Assert.Equal(3, result.Rules[0].Matched);
		Assert.Equal(fixture.SolutionPath, result.Workspace, ignoreCase: true);
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
