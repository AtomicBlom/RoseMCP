using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>What one declaration becomes: a new parameter list, and its documentation if that moved too.</summary>
public sealed record DeclarationChange
{
	public required ParameterListSyntax Parameters { get; init; }

	/// <summary>
	/// Replacement leading trivia, or null when the documentation needed nothing. Null is the common
	/// case: a member that documents no parameters has no tags to keep in step.
	/// </summary>
	public SyntaxTriviaList? Documentation { get; init; }
}
