namespace RoseMcp.Contracts;

/// <summary>A position in source, one-based to match editors and humans.</summary>
public sealed record SourceLocation
{
	public required string FilePath { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	/// <summary>The source line itself, so a caller can judge a hit without opening the file.</summary>
	public string? Preview { get; init; }

	/// <summary>Set when the location is inside source-generated code rather than a file on disk.</summary>
	public string? GeneratedHintName { get; init; }
}

/// <summary>What a symbol is, in the terms an agent needs before changing it.</summary>
public sealed record SymbolInfoResult
{
	public required long Revision { get; init; }

	public required string Name { get; init; }

	public required string Kind { get; init; }

	/// <summary>Fully qualified signature, including parameters and return type.</summary>
	public required string Signature { get; init; }

	public required string Accessibility { get; init; }

	public string? ContainingType { get; init; }

	public string? Namespace { get; init; }

	/// <summary>XML documentation comment, when the symbol has one.</summary>
	public string? Documentation { get; init; }

	/// <summary>
	/// Where the symbol is declared. Empty for symbols that come from metadata rather than source,
	/// which is also the signal that renaming it is not possible.
	/// </summary>
	public required IReadOnlyList<SourceLocation> Declarations { get; init; }

	/// <summary>False for metadata symbols, which cannot be edited.</summary>
	public required bool IsFromSource { get; init; }
}

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

/// <summary>Symbols matching a search.</summary>
public sealed record SymbolSearchResult
{
	public required long Revision { get; init; }

	public required IReadOnlyList<SymbolMatch> Matches { get; init; }

	public required int TotalCount { get; init; }

	public required bool Truncated { get; init; }
}

public sealed record SymbolMatch
{
	public required string Name { get; init; }

	public required string Kind { get; init; }

	public required string Signature { get; init; }

	public required string Project { get; init; }

	public SourceLocation? Location { get; init; }
}
