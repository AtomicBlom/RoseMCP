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
		Assert.Equal([1, 2, 1, 2, 1, 1, 2], result.Rules.Select(rule => rule.Matched));
		Assert.Equal(1, result.Rules[3].Outranked);
		Assert.Equal(10, result.SitesMatched);

		// The precision Equal, the ignore-case Contains, DoesNotContain, Single and All: five calls into
		// Assert, each named by the overload it calls so the gap in the catalog is listed, not guessed.
		Assert.Equal(5, result.SitesUnmatched);
		Assert.Contains(result.Unmatched, group => group.Method.Contains("Equal(double, double, int)", StringComparison.Ordinal));
		Assert.Contains(result.Unmatched, group => group.Method.Contains(".Single", StringComparison.Ordinal));
		Assert.Contains("Preview only; nothing was written to disk.", result.Notices);
	}

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
	private static Task<PatternRewriteResult> PreviewAsync(WorkspaceSession session, string[] filePaths, params PatternRule[] rules)
	{
		var request = new ReplacePatternRequest { Rules = rules, FilePaths = filePaths, Apply = false };

		return session.MutateAsync(
			(snapshot, token) => ReplacePatternService.ReplaceAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}
}
