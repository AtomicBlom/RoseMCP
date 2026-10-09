using System.Text.Json;

using RoseMcp.Broker;
using RoseMcp.Broker.Tools;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The result a structural rewrite returns stays readable at the size a mass rewrite runs to.
/// <para>
/// The numbers are a real migration's preview: 44 rules over three test projects, 3,859 sites in 161
/// files, whose result a client refused at 57,875 characters. A result that grew with them spends a
/// caller's context on the sites that went right, which is the answer the summary exists to replace,
/// and past the client's limit it is not read at all.
/// </para>
/// </summary>
public sealed class PatternReportTests
{
	/// <summary>
	/// The whole result as the caller receives it, as JSON, stays under this at the scale of the
	/// migration, where it measures 37,317. The same sites with every changed file named, three overloads
	/// on every rule and every method on the rules' types listed come to about 65,000, past what the
	/// client accepted. A ratchet: a field that grows with the files, the rules or the misses fails it,
	/// and a change that shrinks the result lowers it.
	/// </summary>
	private const int Ceiling = 38_000;

	/// <summary>
	/// A preview at the migration's scale: every list capped or trimmed, every cut said in a notice, and
	/// the whole result, attribution and changed files included, within the ceiling.
	/// </summary>
	[Test]
	public void A_preview_at_the_scale_of_a_migration_stays_within_the_ceiling()
	{
		const int Rules = 44;
		const int Matching = 32;
		const int Files = 161;

		// Rules 1 to 32 share the 3,859 sites, unevenly, as a catalog does; 33 to 44 match nothing here.
		var sites = Enumerable.Range(0, 3_859).Select(index =>
		{
			var rule = 1 + (index % 97 % Matching);
			var skipped = index % 23 == 0;

			return new SiteRecord
			{
				Rule = rule,
				Location = Location(index % Files, 10 + index),
				Before = $"Assert.Equal(expected{index}.Bounds, actual.Bounds)",
				After = $"actual.Bounds.ShouldBe(expected{index}.Bounds)",
				AlsoMatched = rule % 5 == 0 ? [rule + 1] : [],
				SkippedId = skipped ? $"CS{1500 + (index % 40)}" : null,
				SkippedReason = skipped ? "Argument 2: cannot convert from 'System.Collections.Generic.IEnumerable<Drawboard.Projects.Domain.Annotation>' to 'Drawboard.Projects.Domain.Annotation'" : null,
			};
		}).ToList();

		// Other overloads of the bound methods, and methods on the same types no rule is written for.
		var misses = Enumerable.Range(0, 120)
			.Select(index => Miss($"Equal{index % 4}", "Equal", index))
			.Concat(Enumerable.Range(0, 300).Select(index => Miss($"Unnamed{index % 15}", $"Unnamed{index % 15}", index)))
			.ToList();

		var boundTo = Enumerable.Range(1, Rules).ToDictionary(
			rule => rule,
			rule => (IReadOnlyList<string>)[.. Overloads.Select(parameters => $"Xunit.Assert.{Method(rule)}<T>({parameters})")]);

		var groups = Enumerable.Range(1, Rules).Select(rule => $"Xunit.Assert.{Method(rule)}").Append("Xunit.Assert.Equal").ToHashSet(StringComparer.Ordinal);

		var summary = PatternReport.Build(Rules, boundTo, groups, sites, misses, preview: true);

		summary.Skipped.Count.ShouldBe(PatternReport.Groups);
		summary.Files.Count.ShouldBeLessThanOrEqualTo(PatternReport.FileRows);
		summary.FileCount.ShouldBe(Files);
		summary.UnmatchedCount.ShouldBe(420);
		summary.Unmatched.Sum(group => group.Count).ShouldBe(120);
		summary.Rules.Skip(Matching).ShouldAllBe(rule => rule.Matched == 0 && rule.Sample == null && rule.BoundTo.Count == 1 && rule.Overloads == 4);
		summary.Notices.ShouldContain(notice => notice.Contains(" more skipped groups, covering ", StringComparison.Ordinal));
		summary.Notices.ShouldContain(notice => notice.StartsWith("300 unmatched call(s), to 15 method(s)", StringComparison.Ordinal));

		var changed = Enumerable.Range(0, Files).Select(file => Location(file, 1).FilePath).ToList();
		changed.ShouldAllBe(path => path.Length >= 120 && path.Length <= 135, "the migration's paths ran to about 127 characters");

		var result = WriteForCaller.Shape(WritePaths.Relative(Result(summary, changed), Path.GetDirectoryName(Workspace)!), includeDiff: false);

		result.FilesChanged.ShouldBe(Files);
		result.ChangedFiles.Count.ShouldBe(WriteForCaller.ChangedFileRows);

		// Measured as the result goes out: the SDK's own options, which is what a caller is charged for.
		var size = Size(result);
		var breakdown = $"rules {Size(result.Rules)}, skipped {Size(result.Skipped)}, unmatched {Size(result.Unmatched)}, "
			+ $"files {Size(result.Files)}, changedFiles {Size(result.ChangedFiles)}, notices {Size(result.Notices)}";

		size.ShouldBeLessThan(Ceiling, $"The result is {size} characters of JSON: {breakdown}");
	}

	/// <summary>
	/// A site counts as rewritten only when its replacement was built and not skipped, so the counts
	/// add up: matched is rewritten plus skipped plus any whose replacement was never built.
	/// </summary>
	[Test]
	public void Counts_each_site_once_by_what_happened_to_it()
	{
		SiteRecord[] sites =
		[
			new() { Rule = 1, Location = Location(0, 1), Before = "a", After = "b" },
			new() { Rule = 1, Location = Location(0, 2), Before = "a", After = "b", SkippedId = "CS1061", SkippedReason = "no" },
			new() { Rule = 2, Location = Location(1, 3), Before = "a", AlsoMatched = [3] },
		];

		var summary = PatternReport.Build(3, new Dictionary<int, IReadOnlyList<string>>(), new HashSet<string>(), sites, [], preview: true);

		((summary.Matched, summary.Rewritten, summary.SkippedCount)).ShouldBe((3, 1, 1));
		summary.Rules.Select(rule => rule.Matched).ShouldBe([2, 1, 0]);
		summary.Rules[2].Outranked.ShouldBe(1);
		(summary.Rules[0].Sample?.After).ShouldBe("b");
	}

	/// <summary>A file with something left in it comes before a file that was only rewritten.</summary>
	[Test]
	public void Lists_files_with_something_left_in_them_first()
	{
		SiteRecord[] sites =
		[
			new() { Rule = 1, Location = Location(0, 1), Before = "a", After = "b" },
			new() { Rule = 1, Location = Location(0, 2), Before = "a", After = "b" },
			new() { Rule = 1, Location = Location(1, 3), Before = "a", After = "b", SkippedId = "CS1061", SkippedReason = "no" },
		];

		var summary = PatternReport.Build(1, new Dictionary<int, IReadOnlyList<string>>(), new HashSet<string>(), sites, [], preview: false);

		summary.Files.Select(file => file.FilePath).ShouldBe([Location(1, 0).FilePath, Location(0, 0).FilePath]);
		summary.Rules[0].Sample.ShouldBeNull();
	}

	/// <summary>
	/// A rule that matched nothing names one overload and still says how many it covers, while a rule
	/// that matched keeps the first few: what a rule that took nothing needs to say is that it bound.
	/// </summary>
	[Test]
	public void A_rule_that_matched_nothing_names_one_overload()
	{
		IReadOnlyList<string> overloads = ["Xunit.Assert.Throws<T>(System.Action)", "Xunit.Assert.Throws<T>(System.Func<object>)", "Xunit.Assert.Throws(System.Type, System.Action)", "Xunit.Assert.Throws(System.Type, System.Func<object>)"];

		var summary = PatternReport.Build(
			2,
			new Dictionary<int, IReadOnlyList<string>> { [1] = overloads, [2] = overloads },
			new HashSet<string>(),
			[new() { Rule = 1, Location = Location(0, 1), Before = "a", After = "b" }],
			[],
			preview: true);

		summary.Rules[0].BoundTo.Count.ShouldBe(PatternReport.Overloads);
		summary.Rules[0].Sample.ShouldNotBeNull();
		summary.Rules[1].BoundTo.ShouldBe([overloads[0]]);
		summary.Rules[1].Overloads.ShouldBe(4);
		summary.Rules[1].Sample.ShouldBeNull();
	}

	/// <summary>
	/// An unmatched call to another overload of a method a rule binds is listed -- the precision
	/// <c>Equal</c> beside a rule for the two-argument one -- while a call to a method no rule names is
	/// counted, said in a notice, and kept out of the files, so a narrow catalog over a wide scope does
	/// not list every other method on the same types.
	/// </summary>
	[Test]
	public void Lists_unmatched_calls_only_to_methods_a_rule_binds()
	{
		MissRecord[] misses =
		[
			new("Xunit.Assert.Equal(double, double, int)", "Xunit.Assert.Equal", Location(0, 1), Binds: true),
			new("Xunit.Assert.Single(System.Collections.IEnumerable)", "Xunit.Assert.Single", Location(1, 2), Binds: true),
			new("Xunit.Assert.Single(System.Collections.IEnumerable)", "Xunit.Assert.Single", Location(1, 3), Binds: true),
			new("Xunit.Assert.All<T>(System.Collections.Generic.IEnumerable<T>, System.Action<T>)", "Xunit.Assert.All", Location(1, 4), Binds: true),
		];

		var summary = PatternReport.Build(
			1,
			new Dictionary<int, IReadOnlyList<string>> { [1] = ["Xunit.Assert.Equal<T>(T, T)"] },
			new HashSet<string>(StringComparer.Ordinal) { "Xunit.Assert.Equal" },
			[new() { Rule = 1, Location = Location(2, 1), Before = "a", After = "b" }],
			misses,
			preview: false);

		summary.UnmatchedCount.ShouldBe(4);
		summary.Unmatched.ShouldHaveSingleItem().Method.ShouldBe("Xunit.Assert.Equal(double, double, int)");
		summary.Notices.ShouldContain("3 unmatched call(s), to 2 method(s) on the rules' types that no rule is written for, are counted in sitesUnmatched and left out of unmatched and files.");
		summary.Files.Select(file => file.FilePath).ShouldBe([Location(0, 0).FilePath, Location(2, 0).FilePath]);
		summary.FileCount.ShouldBe(2);
	}

	/// <summary>Every unmatched call is to a method a rule binds, so nothing is left out and nothing is said about it.</summary>
	[Test]
	public void Says_nothing_about_unlisted_calls_when_there_are_none()
	{
		var summary = PatternReport.Build(
			1,
			new Dictionary<int, IReadOnlyList<string>> { [1] = ["Xunit.Assert.Equal<T>(T, T)"] },
			new HashSet<string>(StringComparer.Ordinal) { "Xunit.Assert.Equal" },
			[],
			[new("Xunit.Assert.Equal(double, double, int)", "Xunit.Assert.Equal", Location(0, 1), Binds: true)],
			preview: false);

		summary.Unmatched.ShouldHaveSingleItem();
		summary.Notices.ShouldBeEmpty();
	}

	/// <summary>
	/// A rewrite of a few files names them all, with no notice; one past the cap names the first few,
	/// counts them all in filesChanged and says what it cut, and nothing else in the result moves.
	/// </summary>
	[Test]
	public void Names_changed_files_up_to_a_cap_and_says_when_it_cut()
	{
		var few = Enumerable.Range(0, WriteForCaller.ChangedFileRows).Select(file => Location(file, 1).FilePath).ToList();
		var small = Result(Empty, few) with { Notices = ["Rule 1 binds in no project in scope."] };

		WriteForCaller.Shape(small, includeDiff: false).ShouldBeSameAs(small);

		var many = Enumerable.Range(0, 161).Select(file => Location(file, 1).FilePath).ToList();
		var large = Result(Empty, many) with { Notices = ["Rule 1 binds in no project in scope."] };
		var narrowed = WriteForCaller.Shape(large, includeDiff: false);

		narrowed.ChangedFiles.Select(file => file.FilePath).ShouldBe(many.Take(WriteForCaller.ChangedFileRows));
		narrowed.FilesChanged.ShouldBe(161);
		narrowed.Notices.ShouldBe([
			"Rule 1 binds in no project in scope.",
			$"changedFiles names {WriteForCaller.ChangedFileRows} of the 161 files this would write.",
		]);
		(narrowed with { ChangedFiles = large.ChangedFiles, Notices = large.Notices }).ShouldBe(large);

		WriteForCaller.Shape(large with { Applied = true }, includeDiff: false).Notices[^1].ShouldContain("files this wrote.", Case.Sensitive);
	}

	/// <summary>The solution the migration ran in, which the broker names every path under relative to.</summary>
	private const string Workspace = @"D:\Source\drawboard-projects\Drawboard.Projects.slnx";

	/// <summary>What <paramref name="value"/> costs as it goes out: the SDK's own options, which is what a caller is charged for.</summary>
	private static int Size<T>(T value) => JsonSerializer.Serialize(value, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length;

	/// <summary>A summary of nothing, for the tests about what surrounds one.</summary>
	private static readonly PatternSummary Empty = PatternReport.Build(0, new Dictionary<int, IReadOnlyList<string>>(), new HashSet<string>(), [], [], preview: true);

	/// <summary>
	/// The result the worker builds around <paramref name="summary"/>, attributed as the broker attributes
	/// it, with the notices a preview of that size carries.
	/// </summary>
	private static PatternRewriteResult Result(PatternSummary summary, IReadOnlyList<string> changed) => new()
	{
		Workspace = Workspace,
		WorkspaceKey = "Drawboard.Projects-3d11d873",
		Revision = 12,
		Applied = false,
		SitesMatched = summary.Matched,
		SitesRewritten = summary.Rewritten,
		SitesSkipped = summary.SkippedCount,
		SitesUnmatched = summary.UnmatchedCount,
		Rules = summary.Rules,
		Skipped = summary.Skipped,
		Unmatched = summary.Unmatched,
		Files = summary.Files,
		FileCount = summary.FileCount,
		FilesChanged = changed.Count,
		Diff = null,
		ChangedFiles = [.. changed.Select(path => new ChangedFile { FilePath = path, Lines = "12-14" })],
		Verified = true,
		PreexistingErrorCount = 0,
		ProjectsChecked = ["Drawboard.Projects.Domain.Tests", "Drawboard.Projects.Api.Tests", "Drawboard.Projects.Infrastructure.Tests"],
		Notices =
		[
			.. summary.Notices,
		],
	};

	/// <summary>The parameter lists of the overloads each of the migration's rules binds, which run from short to long as xunit's do.</summary>
	private static readonly string[] Overloads = ["T, T", "T, T, string", "T, T, System.Collections.Generic.IEqualityComparer<T>", "T, T, int"];

	/// <summary>The method a rule of the migration's catalog is about: two rules to a method, as specific-then-general pairs are.</summary>
	private static string Method(int rule) => $"Method{(rule - 1) / 2}";

	/// <summary>An unmatched call to an overload of <paramref name="group"/>.</summary>
	private static MissRecord Miss(string overload, string group, int index) => new(
		$"Xunit.Assert.{overload}(System.Collections.Generic.IEnumerable<object>, System.Func<object, bool>)",
		$"Xunit.Assert.{group}",
		Location(index % 161, 5_000 + index),
		Binds: true);

	/// <summary>A location in one of the migration's files, whose paths run to about 127 characters.</summary>
	private static SourceLocation Location(int file, int line) => new()
	{
		FilePath = $@"D:\Source\drawboard-projects\tests\Drawboard.Projects.Domain.Tests\Features\Annotations\Commands\Handlers\Feature{file:D3}HandlerTests.cs",
		Line = line,
		Column = 3,
		Preview = $"Assert.Equal(expected.Annotations[{line}].Bounds, actual.Annotations[{line}].Bounds);",
		IsTestProject = true,
	};
}
