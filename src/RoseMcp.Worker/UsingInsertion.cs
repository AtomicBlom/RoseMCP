using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>What ensuring a set of imports did to one file.</summary>
public sealed record UsingInsertion
{
	public required CompilationUnitSyntax Root { get; init; }

	/// <summary>
	/// Imports that were written in, as <see cref="ImportDirective.Text"/> spells them: a namespace by
	/// name, a static import as <c>static Type</c>, an alias as <c>Alias = Target</c>.
	/// </summary>
	public required IReadOnlyList<string> Added { get; init; }

	/// <summary>
	/// Imports that needed nothing, each with the reason. Reported rather than dropped: "already
	/// imported" and "in scope from a global using you cannot see in this file" look identical from
	/// the outside, and the second is the one that makes a caller doubt the tool worked.
	/// </summary>
	public required IReadOnlyList<string> AlreadyInScope { get; init; }

	public bool Changed => Added.Count > 0;
}
