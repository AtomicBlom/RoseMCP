namespace RoseMcp.Contracts;

/// <summary>What implements, overrides, or derives from a symbol.</summary>
public sealed record ImplementationsResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>
	/// The symbol this was asked about, as an address: pass it back as <c>symbol</c> to any rose_*
	/// tool. Each match carries its own.
	/// </summary>
	public string? Address { get; init; }

	public required string Symbol { get; init; }

	/// <summary>
	/// Which question was answered, since it depends on what the symbol is: derived types for a
	/// class, implementations for an interface, overrides for a virtual member. Reported so a caller
	/// that pointed at the wrong thing can tell.
	/// </summary>
	public required string Relationship { get; init; }

	public required IReadOnlyList<SymbolMatch> Matches { get; init; }

	public required int TotalCount { get; init; }

	public required bool Truncated { get; init; }
}
