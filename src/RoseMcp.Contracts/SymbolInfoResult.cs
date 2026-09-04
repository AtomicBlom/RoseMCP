namespace RoseMcp.Contracts;

/// <summary>What a symbol is, in the terms an agent needs before changing it.</summary>
public sealed record SymbolInfoResult : WorkspaceScopedResult
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

	/// <summary>
	/// The full extent of each declaration, so a caller knows where the member stops without
	/// reading the file to find out. One entry per declaration: a partial has several.
	/// </summary>
	public IReadOnlyList<DeclarationSpan> DeclarationSpans { get; init; } = [];

	/// <summary>
	/// What this member overrides or implements, walking up the hierarchy. The other direction from
	/// rose_find_implementations, and the one that answers "where does this actually come from" for
	/// an override whose base declares the documentation.
	/// </summary>
	public IReadOnlyList<SymbolMatch> BaseDefinitions { get; init; } = [];

	/// <summary>False for metadata symbols, which cannot be edited.</summary>
	public required bool IsFromSource { get; init; }
}
