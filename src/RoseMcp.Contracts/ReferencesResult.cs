namespace RoseMcp.Contracts;

/// <summary>Every reference to a symbol across the solution.</summary>
public sealed record ReferencesResult
{
	public required long Revision { get; init; }

	public required string Symbol { get; init; }

	public required IReadOnlyList<SourceLocation> Definitions { get; init; }

	public required IReadOnlyList<SourceLocation> References { get; init; }

	public required int TotalCount { get; init; }

	public required bool Truncated { get; init; }
}
