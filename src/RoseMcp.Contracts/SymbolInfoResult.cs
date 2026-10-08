using System.Text.Json.Serialization;

namespace RoseMcp.Contracts;

/// <summary>What a symbol is, in the terms an agent needs before changing it.</summary>
public sealed record SymbolInfoResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>
	/// This symbol as an address: pass it back as <c>symbol</c> to any rose_* tool. Null where nothing
	/// can name it -- a local or a parameter is declared inside a member rather than as one.
	/// </summary>
	public string? Address { get; init; }

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

	/// <summary>
	/// The assembly the symbol lives in, when it is not one this solution declares. Where a source
	/// symbol has declarations to point at, a metadata one has only this -- and a caller that knows
	/// which assembly a type came from knows which package to look in and which reference to add.
	/// </summary>
	public string? ContainingAssembly { get; init; }

	/// <summary>
	/// For a type from a referenced assembly, the members code outside that assembly can use: its own
	/// public and protected ones, each with its signature, in the order the assembly declares them.
	/// Absent for anything else -- a type declared in source has rose_outline, and a member has no
	/// members.
	/// <para>
	/// Here because a metadata type has no file for rose_outline to read, and the member names are
	/// exactly what a caller asking about a library type does not know yet. Signatures are always
	/// given, since a metadata member has no line to tell its overloads apart by.
	/// </para>
	/// </summary>
	public IReadOnlyList<OutlinedMember>? Members { get; init; }

	/// <summary>
	/// How many members matched, where <see cref="Members"/> is given: more than it lists when the
	/// listing stopped at its cap, which <see cref="Truncated"/> says.
	/// </summary>
	public int? TotalMembers { get; init; }

	/// <summary>True where <see cref="Members"/> stopped at the cap before listing every match. Absent when false.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool Truncated { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];

	/// <summary>
	/// The declaration's own source text, when it was asked for. One entry per declaration, so a
	/// partial comes back in the several pieces it is written in.
	/// <para>
	/// Here so that understanding a member does not end in a file read. Reading the file is what puts
	/// the file in front of the caller, and the next edit then goes through a text tool -- which is
	/// the moment every other tool here stops being worth reaching for.
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Source { get; init; } = [];
}
