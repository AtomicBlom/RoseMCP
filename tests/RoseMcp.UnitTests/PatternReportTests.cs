using System.Text.Json;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The summary a structural rewrite returns stays readable at the size a mass rewrite runs to.
/// <para>
/// The numbers are the migration this tool was built for: about 3,700 sites in 156 files. A result
/// that grew with them would spend a caller's context on the sites that went right, which is the
/// answer the summary exists to replace.
/// </para>
/// </summary>
public sealed class PatternReportTests
{
	/// <summary>The part of a result the report builds, as JSON, stays under this at acceptance scale.</summary>
	private const int Ceiling = 35_000;

	/// <summary>Groups beyond the cap are counted in a notice rather than listed, and the whole stays small.</summary>
	[Test]
	public void Stays_small_at_the_scale_of_a_mass_rewrite()
	{
		var sites = Enumerable.Range(0, 3_700).Select(index => new SiteRecord
		{
			Rule = 1 + (index % 50),
			Location = Location(index % 156, index),
			Before = $"Assert.Equal(expected{index}, actual{index})",
			After = $"actual{index}.ShouldBe(expected{index})",
			SkippedId = index % 7 == 0 ? $"CS{1000 + (index % 30)}" : null,
			SkippedReason = index % 7 == 0 ? new string('r', 400) : null,
		}).ToList();

		var misses = Enumerable.Range(0, 400)
			.Select(index => new MissRecord($"Xunit.Assert.Method{index % 40}(object)", Location(index % 156, index), Binds: true))
			.ToList();

		var summary = PatternReport.Build(50, Enumerable.Range(1, 50).ToDictionary(rule => rule, _ => (IReadOnlyList<string>)["Xunit.Assert.Equal<T>(T, T)"]), sites, misses, preview: true);

		Assert.Equal(PatternReport.Groups, summary.Skipped.Count);
		Assert.Equal(PatternReport.Groups, summary.Unmatched.Count);
		Assert.True(summary.Files.Count <= PatternReport.FileRows);
		Assert.Equal(156, summary.FileCount);
		Assert.Contains(summary.Notices, notice => notice.Contains(" more skipped groups, covering ", StringComparison.Ordinal));
		Assert.Contains(summary.Notices, notice => notice.StartsWith($"{40 - PatternReport.Groups} more unmatched groups", StringComparison.Ordinal));

		// Measured as the result goes out: the SDK's own options, which is what a caller is charged for.
		var size = JsonSerializer.Serialize(summary, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length;

		Assert.True(size < Ceiling, $"The summary is {size} characters of JSON");
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

		var summary = PatternReport.Build(3, new Dictionary<int, IReadOnlyList<string>>(), sites, [], preview: true);

		Assert.Equal((3, 1, 1), (summary.Matched, summary.Rewritten, summary.SkippedCount));
		Assert.Equal([2, 1, 0], summary.Rules.Select(rule => rule.Matched));
		Assert.Equal(1, summary.Rules[2].Outranked);
		Assert.Equal("b", summary.Rules[0].Sample?.After);
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

		var summary = PatternReport.Build(1, new Dictionary<int, IReadOnlyList<string>>(), sites, [], preview: false);

		Assert.Equal([@"C:\repo\File1.cs", @"C:\repo\File0.cs"], summary.Files.Select(file => file.FilePath));
		Assert.Null(summary.Rules[0].Sample);
	}

	/// <summary>A location in one of a few files.</summary>
	private static SourceLocation Location(int file, int line) => new()
	{
		FilePath = $@"C:\repo\File{file}.cs",
		Line = line,
		Column = 3,
		Preview = $"Assert.Equal(expected{line}, actual{line});",
	};
}
