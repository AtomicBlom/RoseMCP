namespace RoseMcp.Contracts;

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
