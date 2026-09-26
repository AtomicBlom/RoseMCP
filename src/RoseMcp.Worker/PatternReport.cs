using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Turns every site a structural rewrite met into the summary a caller reads, held to a size a
/// caller can read.
/// <para>
/// A pure function of what happened, so the caps are tested on their own: a rewrite of thousands of
/// sites that fails somewhere in the middle is exactly the case where an uncapped answer would spend
/// the caller's context on the parts that went right.
/// </para>
/// </summary>
public static class PatternReport
{
	/// <summary>How many skip or unmatched groups are listed; the rest are counted in a notice.</summary>
	public const int Groups = 20;

	/// <summary>How many example locations a group carries.</summary>
	public const int Examples = 2;

	/// <summary>How many files are listed.</summary>
	public const int FileRows = 50;

	/// <summary>How many of the most rewritten files are listed after the ones with something to look at.</summary>
	public const int TopRewritten = 10;

	/// <summary>How long a sample's before or after may run before it is cut.</summary>
	public const int SampleCut = 120;

	/// <summary>How long a skip reason may run before it is cut: long enough for the compiler's message about one site.</summary>
	public const int ReasonCut = 160;

	/// <summary>The summary of <paramref name="sites"/> and <paramref name="misses"/>.</summary>
	/// <param name="ruleCount">How many rules there are, so a rule that matched nothing is still listed.</param>
	/// <param name="boundTo">The overloads each rule covers, by rule number, as addresses.</param>
	/// <param name="sites">Every site a rule won.</param>
	/// <param name="misses">Every call into the rules' types that no rule matched.</param>
	/// <param name="preview">Whether this is a preview, which is when a sample is worth its space.</param>
	public static PatternSummary Build(
		int ruleCount,
		IReadOnlyDictionary<int, IReadOnlyList<string>> boundTo,
		IReadOnlyList<SiteRecord> sites,
		IReadOnlyList<MissRecord> misses,
		bool preview)
	{
		var notices = new List<string>();

		var rules = Enumerable.Range(1, ruleCount).Select(number => Rule(number, boundTo, sites, preview)).ToList();

		var skipGroups = sites
			.Where(site => site.SkippedId is not null)
			.GroupBy(site => (site.Rule, Id: site.SkippedId!))
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key.Rule)
			.ToList();

		var skipped = skipGroups.Take(Groups).Select(group => new PatternSkipGroup
		{
			Rule = group.Key.Rule,
			DiagnosticId = group.Key.Id,
			Reason = Shortened(group.First().SkippedReason ?? string.Empty, ReasonCut),
			Count = group.Count(),
			Examples = [.. group.Take(Examples).Select(site => site.Location)],
		}).ToList();

		Overflow(notices, skipGroups.Count, skipGroups.Skip(Groups).Sum(group => group.Count()), "skipped");

		var missGroups = misses
			.GroupBy(miss => miss.Method, StringComparer.Ordinal)
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key, StringComparer.Ordinal)
			.ToList();

		var unmatched = missGroups.Take(Groups).Select(group => new PatternUnmatchedGroup
		{
			Method = group.Key,
			Count = group.Count(),
			DoNotCompile = group.Count(miss => !miss.Binds),
			Examples = [.. group.Take(Examples).Select(miss => miss.Location)],
		}).ToList();

		Overflow(notices, missGroups.Count, missGroups.Skip(Groups).Sum(group => group.Count()), "unmatched");

		var (files, fileCount) = Files(sites, misses);

		if (fileCount > files.Count)
		{
			notices.Add($"{files.Count} of the {fileCount} files a rule matched in are listed: every one with a skipped or "
				+ "unmatched site first, then the ones rewritten most.");
		}

		return new PatternSummary
		{
			Rules = rules,
			Skipped = skipped,
			Unmatched = unmatched,
			Files = files,
			FileCount = fileCount,
			Matched = sites.Count,
			Rewritten = sites.Count(IsRewritten),
			SkippedCount = sites.Count(site => site.SkippedId is not null),
			UnmatchedCount = misses.Count,
			Notices = notices,
		};
	}

	/// <summary>Whether a site's replacement was written, or would be.</summary>
	private static bool IsRewritten(SiteRecord site) => site.SkippedId is null && site.After is not null;

	/// <summary>What one rule did.</summary>
	private static PatternRuleOutcome Rule(
		int number,
		IReadOnlyDictionary<int, IReadOnlyList<string>> boundTo,
		IReadOnlyList<SiteRecord> sites,
		bool preview)
	{
		var won = sites.Where(site => site.Rule == number).ToList();
		var sample = preview ? won.FirstOrDefault(IsRewritten) : null;

		return new PatternRuleOutcome
		{
			Rule = number,
			BoundTo = boundTo.GetValueOrDefault(number) ?? [],
			Matched = won.Count,
			Rewritten = won.Count(IsRewritten),
			Skipped = won.Count(site => site.SkippedId is not null),
			Outranked = sites.Count(site => site.AlsoMatched.Contains(number)),
			Sample = sample is null
				? null
				: new PatternSample
				{
					Location = $"{sample.Location.FilePath}:{sample.Location.Line}",
					Before = Shortened(sample.Before, SampleCut),
					After = Shortened(sample.After!, SampleCut),
				},
		};
	}

	/// <summary>
	/// The files worth a caller's attention -- every one with something left in it, then the ones
	/// rewritten most -- and how many files there were in all.
	/// </summary>
	private static (IReadOnlyList<PatternFileOutcome> Files, int Count) Files(IReadOnlyList<SiteRecord> sites, IReadOnlyList<MissRecord> misses)
	{
		var paths = sites.Select(site => site.Location.FilePath)
			.Concat(misses.Select(miss => miss.Location.FilePath))
			.Distinct(StringComparer.OrdinalIgnoreCase);

		var all = paths.Select(path => new PatternFileOutcome
		{
			FilePath = path,
			Rewritten = sites.Count(site => IsRewritten(site) && Same(site.Location.FilePath, path)),
			Skipped = sites.Count(site => site.SkippedId is not null && Same(site.Location.FilePath, path)),
			Unmatched = misses.Count(miss => Same(miss.Location.FilePath, path)),
		}).ToList();

		var attention = all
			.Where(file => file.Skipped > 0 || file.Unmatched > 0)
			.OrderByDescending(file => file.Skipped + file.Unmatched)
			.ThenBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase);

		var busiest = all
			.Where(file => file.Skipped == 0 && file.Unmatched == 0)
			.OrderByDescending(file => file.Rewritten)
			.Take(TopRewritten);

		return ([.. attention.Concat(busiest).Take(FileRows)], all.Count);
	}

	/// <summary>A notice for the groups beyond the cap, when there are any.</summary>
	private static void Overflow(List<string> notices, int groups, int sites, string what)
	{
		if (groups <= Groups) return;

		notices.Add($"{groups - Groups} more {what} groups, covering {sites} site(s), are not listed. Narrow filePaths to see them.");
	}

	/// <summary>Whether two paths name the same file.</summary>
	private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

	/// <summary>Text cut to <paramref name="limit"/> characters, saying so when it was.</summary>
	private static string Shortened(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";
}

/// <summary>One site a rule won, as the report needs it.</summary>
public sealed record SiteRecord
{
	/// <summary>The rule that won it, from one.</summary>
	public required int Rule { get; init; }

	/// <summary>Where it is.</summary>
	public required SourceLocation Location { get; init; }

	/// <summary>The code it matched.</summary>
	public required string Before { get; init; }

	/// <summary>What it becomes, or null when no replacement was built.</summary>
	public string? After { get; init; }

	/// <summary>The later rules that also matched it.</summary>
	public IReadOnlyList<int> AlsoMatched { get; init; } = [];

	/// <summary>The compiler's id for why its replacement was not written, or null when it was.</summary>
	public string? SkippedId { get; init; }

	/// <summary>The compiler's message for it.</summary>
	public string? SkippedReason { get; init; }
}

/// <summary>A call into one of the rules' types that no rule matched.</summary>
/// <param name="Method">The method it calls, as an address.</param>
/// <param name="Location">Where it is.</param>
/// <param name="Binds">Whether it compiles at all.</param>
public sealed record MissRecord(string Method, SourceLocation Location, bool Binds);

/// <summary>The parts of a result that <see cref="PatternReport"/> works out.</summary>
public sealed record PatternSummary
{
	/// <summary>Each rule, in order.</summary>
	public required IReadOnlyList<PatternRuleOutcome> Rules { get; init; }

	/// <summary>The skip groups that are listed, largest first.</summary>
	public required IReadOnlyList<PatternSkipGroup> Skipped { get; init; }

	/// <summary>The unmatched groups that are listed, largest first.</summary>
	public required IReadOnlyList<PatternUnmatchedGroup> Unmatched { get; init; }

	/// <summary>The files that are listed.</summary>
	public required IReadOnlyList<PatternFileOutcome> Files { get; init; }

	/// <summary>How many files a site or an unmatched call was in, listed or not.</summary>
	public required int FileCount { get; init; }

	/// <summary>Every site a rule won.</summary>
	public required int Matched { get; init; }

	/// <summary>The sites whose replacement was written, or would be.</summary>
	public required int Rewritten { get; init; }

	/// <summary>The sites whose replacement would not compile.</summary>
	public required int SkippedCount { get; init; }

	/// <summary>Every unmatched call, listed or not.</summary>
	public required int UnmatchedCount { get; init; }

	/// <summary>What the caps left out, said in words.</summary>
	public required IReadOnlyList<string> Notices { get; init; }
}
