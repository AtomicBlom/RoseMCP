namespace RoseMcp.Contracts;

/// <summary>Symbols matching a search.</summary>
public sealed record SymbolSearchResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required IReadOnlyList<SymbolMatch> Matches { get; init; }

	public required int TotalCount { get; init; }

	public required bool Truncated { get; init; }
}
