using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// The parameter list of a declaration that has one, whichever kind of declaration it is.
/// <para>
/// Two syntax kinds carry one, and they do not share a base class. A method, a constructor and an
/// operator are all <see cref="BaseMethodDeclarationSyntax"/>; a primary constructor's parameters
/// are written on the <em>type</em>, so its declaration is a
/// <see cref="TypeDeclarationSyntax"/> whose <c>ParameterList</c> may or may not be there. A
/// primary constructor is the most common shape of the member a parameter is most often added to,
/// so treating it as "not a method" refuses the ordinary case.
/// </para>
/// </summary>
public static class ParameterLists
{
	/// <summary>The declaration's parameter list, or null when it has none to change.</summary>
	public static ParameterListSyntax? Of(SyntaxNode? node) => node switch
	{
		BaseMethodDeclarationSyntax method => method.ParameterList,
		TypeDeclarationSyntax type => type.ParameterList,
		_ => null,
	};

	/// <summary>
	/// The same declaration with a different parameter list. Returns the node unchanged when it has
	/// no parameter list, so a caller that has already asked <see cref="Of"/> does not have to ask
	/// again.
	/// </summary>
	public static SyntaxNode With(SyntaxNode node, ParameterListSyntax parameters) => node switch
	{
		BaseMethodDeclarationSyntax method => method.WithParameterList(parameters),
		TypeDeclarationSyntax type => type.WithParameterList(parameters),
		_ => node,
	};
}
