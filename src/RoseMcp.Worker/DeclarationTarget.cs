using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>One declaration, and everything needed to write over it.</summary>
public sealed record DeclarationTarget
{
	public required ISymbol Symbol { get; init; }

	public required Document Document { get; init; }

	/// <summary>
	/// The declaration as written. Not always the node the symbol points at: a field symbol's own
	/// syntax is its variable declarator, and what a caller means by the member is the field
	/// declaration around it.
	/// </summary>
	public required MemberDeclarationSyntax Declaration { get; init; }

	public string FilePath => Document.FilePath!;

	public string Signature => SymbolSignature.Of(Symbol);
}
