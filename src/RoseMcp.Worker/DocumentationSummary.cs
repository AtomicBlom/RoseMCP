using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Keeps a declaration's <c>&lt;summary&gt;</c> when the documentation comment replacing it has none.
/// <para>
/// A replacement's doc comment replaces the old one, and that is right for everything else in it: a
/// parameter list rewritten with the member has to lose the old <c>&lt;param&gt;</c> tags, or they name
/// parameters that are gone. The summary is different. Every documented member has one, a caller adding
/// <c>&lt;remarks&gt;</c> or a <c>&lt;param&gt;</c> to a member is not asking to delete it, and a doc
/// comment without one is well-formed -- so its loss compiles, passes every analyzer that does not insist
/// on summaries, and is visible only to somebody reading the diff line by line.
/// </para>
/// <para>
/// A comment that inherits its documentation is left alone. <c>&lt;inheritdoc/&gt;</c> is the one
/// deliberate way to have no summary, and putting the old one back over it would override what it
/// inherits.
/// </para>
/// </summary>
public static class DocumentationSummary
{
	/// <summary>
	/// <paramref name="supplied"/>, with <paramref name="existing"/>'s summary put first in its
	/// documentation comment where it has none of its own; otherwise <paramref name="supplied"/> unchanged.
	/// </summary>
	/// <param name="supplied">The replacement's leading trivia, beginning at its first comment.</param>
	/// <param name="existing">The declaration's leading trivia as the file has it.</param>
	/// <param name="kept">Whether the old summary was carried over.</param>
	public static IReadOnlyList<SyntaxTrivia> KeptInto(
		IReadOnlyList<SyntaxTrivia> supplied,
		SyntaxTriviaList existing,
		out bool kept)
	{
		kept = false;

		var index = IndexOfDocumentation(supplied);
		if (index < 0) return supplied;

		var comment = (DocumentationCommentTriviaSyntax)supplied[index].GetStructure()!;
		var hasOwnSummary = Elements(comment).Any(name => name is "summary" or "inheritdoc");
		if (hasOwnSummary) return supplied;

		var old = existing
			.Select(trivia => trivia.GetStructure())
			.OfType<DocumentationCommentTriviaSyntax>()
			.FirstOrDefault();

		if (old?.Content.OfType<XmlElementSyntax>().FirstOrDefault(IsSummary) is not { } summary) return supplied;

		var oldText = old.ToFullString();
		var ending = oldText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

		var text = string.Concat(supplied.Take(index).Select(trivia => trivia.ToFullString()))
			+ "/// " + summary.ToString() + ending
			+ IndentationBefore(existing)
			+ string.Concat(supplied.Skip(index).Select(trivia => trivia.ToFullString()));

		kept = true;

		return [.. SyntaxFactory.ParseLeadingTrivia(text)];
	}

	private static int IndexOfDocumentation(IReadOnlyList<SyntaxTrivia> trivia)
	{
		for (var i = 0; i < trivia.Count; i++)
		{
			if (trivia[i].IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)) return i;
		}

		return -1;
	}

	/// <summary>The names of the top-level elements in a documentation comment, empty ones included.</summary>
	private static IEnumerable<string> Elements(DocumentationCommentTriviaSyntax comment) =>
		comment.Content.Select(node => node switch
		{
			XmlElementSyntax element => element.StartTag.Name.LocalName.Text,
			XmlEmptyElementSyntax empty => empty.Name.LocalName.Text,
			_ => string.Empty,
		});

	private static bool IsSummary(XmlElementSyntax element) => element.StartTag.Name.LocalName.Text == "summary";

	/// <summary>
	/// The indentation the declaration's own comment starts at, which is what the line after the
	/// carried summary needs: the replacement is written beneath the declaration's leading trivia, so its
	/// first line takes that indentation and the second has to be given it.
	/// </summary>
	private static string IndentationBefore(SyntaxTriviaList existing)
	{
		var indentation = string.Empty;

		foreach (var trivia in existing)
		{
			if (MemberSyntax.IsComment(trivia)) break;

			indentation = trivia.IsKind(SyntaxKind.WhitespaceTrivia) ? trivia.ToFullString() : string.Empty;
		}

		return indentation;
	}
}
