namespace RoseMcp.Contracts;

/// <summary>Symbols matching a search, closest names first.</summary>
public sealed record SymbolSearchResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>The closest matches, up to <c>maxResults</c>.</summary>
	public required IReadOnlyList<SymbolMatch> Matches { get; init; }

	/// <summary>How many matched, listed or not.</summary>
	public required int TotalCount { get; init; }

	/// <summary>True where more matched than <c>maxResults</c>, so <see cref="Shape"/> describes all of them.</summary>
	public required bool Truncated { get; init; }

	/// <summary>
	/// How every match divides by kind and project, given where the cap left some out, so the ones it
	/// hid can be asked for by the group they are in.
	/// </summary>
	public SymbolSearchShape? Shape { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}

/// <summary>
/// How a search's matches divide. Every group is keyed by the value the narrowing argument of the same
/// name takes, so a group read here is a question the next call can ask.
/// </summary>
public sealed record SymbolSearchShape
{
	/// <summary>Every kind matched, most first.</summary>
	public required IReadOnlyList<SymbolKindCount> Kinds { get; init; }

	/// <summary>Every project with a match, most first.</summary>
	public required IReadOnlyList<SymbolProjectCount> Projects { get; init; }
}

/// <summary>How many matches are of one kind.</summary>
public sealed record SymbolKindCount
{
	public required string Kind { get; init; }

	public required int Count { get; init; }
}

/// <summary>How many matches one project declares.</summary>
public sealed record SymbolProjectCount
{
	public required string Project { get; init; }

	public required int Count { get; init; }
}
