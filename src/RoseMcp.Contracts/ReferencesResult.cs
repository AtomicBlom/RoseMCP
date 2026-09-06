namespace RoseMcp.Contracts;

/// <summary>Every reference to a symbol across the solution.</summary>
public sealed record ReferencesResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>
	/// The symbol these are references to, as an address: pass it back as <c>symbol</c> to any rose_*
	/// tool. Not repeated per reference, because a location already names the member it sits inside and
	/// an address on each would grow the very answer the narrowing arguments exist to shrink.
	/// </summary>
	public string? Address { get; init; }

	public required string Symbol { get; init; }

	public required IReadOnlyList<SourceLocation> Definitions { get; init; }

	public required IReadOnlyList<SourceLocation> References { get; init; }

	public required int TotalCount { get; init; }

	public required bool Truncated { get; init; }
}
