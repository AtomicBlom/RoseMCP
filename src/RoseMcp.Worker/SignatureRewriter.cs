using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// Applies a signature change to one document: the declarations in it, and the call sites in it.
/// <para>
/// A rewriter rather than a sequence of node replacements because the two can nest. A method that
/// calls itself, or calls the overload next to it, has a call site inside a declaration that is
/// itself being rewritten -- and a rewriter, which rebuilds from the leaves up, gets that right
/// without anything having to be found again by position after an earlier edit moved it.
/// </para>
/// </summary>
public sealed class SignatureRewriter(
	IReadOnlyDictionary<TextSpan, DeclarationChange> declarations,
	IReadOnlyDictionary<TextSpan, int> callSites,
	ParameterPlan plan,
	IReadOnlyDictionary<string, string> supplied,
	SyntaxAnnotation marker) : CSharpSyntaxRewriter
{
	/// <summary>Call sites whose arguments could not be rewritten safely, by their original span.</summary>
	public HashSet<TextSpan> Refused { get; } = [];

	/// <summary>Call sites that were rewritten, by their original span.</summary>
	public HashSet<TextSpan> Rewritten { get; } = [];

	public override SyntaxNode? Visit(SyntaxNode? node)
	{
		var visited = base.Visit(node);
		if (node is null || visited is null) return visited;

		if (declarations.TryGetValue(node.Span, out var change) && visited is BaseMethodDeclarationSyntax declaration)
		{
			// The parameter list carries the annotation rather than the declaration, so the whitespace
			// pass afterwards owns the lines that were written and not the whole member body.
			var updated = declaration.WithParameterList(change.Parameters.WithAdditionalAnnotations(marker));

			return change.Documentation is { } documentation ? updated.WithLeadingTrivia(documentation) : updated;
		}

		if (!callSites.TryGetValue(node.Span, out var skip) || visited is not ArgumentListSyntax arguments) return visited;

		var rewritten = CallSiteRewriter.Rewrite(arguments, plan, supplied, skip);

		if (rewritten is null)
		{
			Refused.Add(node.Span);
			return visited;
		}

		Rewritten.Add(node.Span);

		return rewritten;
	}
}
