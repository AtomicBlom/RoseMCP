using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>What one declaration becomes: a new parameter list, a new accessibility, or both, and its documentation if that moved too.</summary>
public sealed record DeclarationChange
{
	/// <summary>The new parameter list, or null where the parameters are not changing.</summary>
	public ParameterListSyntax? Parameters { get; init; }

	/// <summary>The keywords of the new accessibility, or null where it is not changing.</summary>
	public IReadOnlyList<SyntaxKind>? Accessibility { get; init; }

	/// <summary>
	/// Replacement leading trivia, or null when the documentation needed nothing. Null is the common
	/// case: a member that documents no parameters has no tags to keep in step.
	/// </summary>
	public SyntaxTriviaList? Documentation { get; init; }
}
