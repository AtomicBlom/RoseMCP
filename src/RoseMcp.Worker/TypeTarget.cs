using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>One type declaration, and everything needed to add a member to it.</summary>
public sealed record TypeTarget
{
	public required INamedTypeSymbol Symbol { get; init; }

	public required Document Document { get; init; }

	/// <summary>
	/// The declaration to write into. A partial type has several, and which one a member should join
	/// is the caller's decision rather than a coin toss, so this is only ever reached once exactly
	/// one of them has been settled on.
	/// </summary>
	public required BaseTypeDeclarationSyntax Declaration { get; init; }

	public string FilePath => Document.FilePath!;

	public string Signature => SymbolSignature.Of(Symbol);
}
