namespace RoseMcp.Contracts;

/// <summary>
/// What a structural rewrite did, told as counts rather than as a diff.
/// <para>
/// A mass rewrite runs to thousands of sites, and a diff of that size is not something a caller can
/// read -- it spends the context it was meant to inform. What a caller needs is whether the rules did
/// what they were written for: how many sites each took, which overloads each reaches, which sites
/// were left and why, and which calls into the same types nothing took at all. The last is how a
/// catalog is known to be complete, because matched and unmatched calls together are every call.
/// </para>
/// </summary>
public sealed record PatternRewriteResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	/// <summary>Every site a rule matched, rewritten or not.</summary>
	public required int SitesMatched { get; init; }

	/// <summary>The sites whose replacement was written, or would be.</summary>
	public required int SitesRewritten { get; init; }

	/// <summary>The sites a rule matched whose replacement would not compile there, and were left as they were.</summary>
	public required int SitesSkipped { get; init; }

	/// <summary>
	/// Calls into the rules' types that no rule matched: every one of them, whether or not
	/// <see cref="Unmatched"/> lists its method.
	/// </summary>
	public required int SitesUnmatched { get; init; }

	/// <summary>Each rule, in order, whether it matched anything or not.</summary>
	public required IReadOnlyList<PatternRuleOutcome> Rules { get; init; }

	/// <summary>The skipped sites, grouped by the rule and the error that stopped them. Capped; the notices say how many more.</summary>
	public required IReadOnlyList<PatternSkipGroup> Skipped { get; init; }

	/// <summary>
	/// The unmatched calls to methods some rule is written for, grouped by the overload they call: a
	/// call to another overload of a method a rule binds is the gap in a catalog. Calls to methods no
	/// rule names are counted in <see cref="SitesUnmatched"/> and a notice rather than listed, since a
	/// narrow catalog over a wide scope would otherwise list every other method on the same types.
	/// Capped; the notices say how many more.
	/// </summary>
	public required IReadOnlyList<PatternUnmatchedGroup> Unmatched { get; init; }

	/// <summary>
	/// The files worth looking at: every one with a skipped site or a call <see cref="Unmatched"/> would
	/// list, then the most rewritten. Capped; <see cref="FileCount"/> is the whole number.
	/// </summary>
	public required IReadOnlyList<PatternFileOutcome> Files { get; init; }

	/// <summary>How many files a rule matched in, or had a call in that <see cref="Unmatched"/> would list.</summary>
	public required int FileCount { get; init; }

	/// <summary>
	/// How many files this wrote, or would write. <see cref="WorkspaceMutationResult.ChangedFiles"/> names
	/// them only up to a cap, with a notice past it: at the scale of a migration the paths are the
	/// largest thing the result would carry, and <see cref="Files"/> already names the ones with
	/// something to read.
	/// </summary>
	public required int FilesChanged { get; init; }

	/// <summary>Whether the changed projects were compiled afterwards.</summary>
	public bool Verified { get; init; }

	/// <summary>Errors that exist after the change and did not before.</summary>
	public IReadOnlyList<DiagnosticEntry> IntroducedDiagnostics { get; init; } = [];

	/// <summary>How many errors the change made go away.</summary>
	public int ResolvedDiagnosticCount { get; init; }

	/// <summary>
	/// Errors in the projects checked that were there before the change and still are.
	/// </summary>
	public int PreexistingErrorCount { get; init; }

	/// <summary>The projects that were compiled to verify it.</summary>
	public IReadOnlyList<string> ProjectsChecked { get; init; } = [];
}

/// <summary>What one rule did.</summary>
public sealed record PatternRuleOutcome
{
	/// <summary>Its place in the list, from one.</summary>
	public required int Rule { get; init; }

	/// <summary>
	/// The overloads its find covers, as addresses: what says which of a method's overloads a rule
	/// reaches, without the caller having to work it out from the text. Only the first for a rule that
	/// matched nothing, which needs no more than that to say what it bound to.
	/// </summary>
	public required IReadOnlyList<string> BoundTo { get; init; }

	/// <summary>How many overloads the rule covers in all, of which <see cref="BoundTo"/> names the first few.</summary>
	public required int Overloads { get; init; }

	/// <summary>The sites it won.</summary>
	public required int Matched { get; init; }

	/// <summary>The sites it won whose replacement was written, or would be.</summary>
	public required int Rewritten { get; init; }

	/// <summary>The sites it won whose replacement would not compile.</summary>
	public required int Skipped { get; init; }

	/// <summary>
	/// The sites it also matched where an earlier rule won. Not a fault -- a general rule after a
	/// specific one is meant to lose those -- but a surprise when it is not what was meant.
	/// </summary>
	public required int Outranked { get; init; }

	/// <summary>One site it matched, before and after, in a preview; absent when it matched nothing.</summary>
	public PatternSample? Sample { get; init; }
}

/// <summary>One site a rule matched, as it was and as it would be.</summary>
public sealed record PatternSample
{
	/// <summary>The site, as path:line.</summary>
	public required string Location { get; init; }

	/// <summary>The code the rule matched.</summary>
	public required string Before { get; init; }

	/// <summary>What it becomes.</summary>
	public required string After { get; init; }
}

/// <summary>Sites one rule matched and could not rewrite, for the same reason.</summary>
public sealed record PatternSkipGroup
{
	/// <summary>The rule, from one.</summary>
	public required int Rule { get; init; }

	/// <summary>The compiler's id for the error the replacement would have introduced.</summary>
	public required string DiagnosticId { get; init; }

	/// <summary>The first such error's message.</summary>
	public required string Reason { get; init; }

	/// <summary>How many sites this is.</summary>
	public required int Count { get; init; }

	/// <summary>A few of them.</summary>
	public required IReadOnlyList<SourceLocation> Examples { get; init; }
}

/// <summary>Calls to one method that no rule matched.</summary>
public sealed record PatternUnmatchedGroup
{
	/// <summary>The method, as an address.</summary>
	public required string Method { get; init; }

	/// <summary>How many calls this is.</summary>
	public required int Count { get; init; }

	/// <summary>How many of them do not compile, and so could not have matched anything.</summary>
	public int DoNotCompile { get; init; }

	/// <summary>A few of them.</summary>
	public required IReadOnlyList<SourceLocation> Examples { get; init; }
}

/// <summary>What happened in one file.</summary>
public sealed record PatternFileOutcome
{
	public required string FilePath { get; init; }

	public required int Rewritten { get; init; }

	public required int Skipped { get; init; }

	public required int Unmatched { get; init; }
}
