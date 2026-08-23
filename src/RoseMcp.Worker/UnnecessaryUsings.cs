using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>What was removed, and the solution with it gone.</summary>
public sealed record UsingCleanup
{
	public required Solution Solution { get; init; }

	/// <summary>The directives dropped, as written, for reporting.</summary>
	public required IReadOnlyList<string> Removed { get; init; }
}

/// <summary>
/// Drops using directives that nothing in a file needs any more.
/// <para>
/// Splitting a file creates these in both directions: the type that left may have been the only
/// user of an import, and the file it landed in inherits the whole list. Leaving them is not
/// cosmetic -- a repository that builds with EnforceCodeStyleInBuild treats an unnecessary using as
/// an error, so a move that ignored this would hand back code that does not compile.
/// </para>
/// <para>
/// The compiler is the authority, via CS8019. Working out which imports a declaration needs by
/// hand means reimplementing name binding, and getting extension methods and aliases wrong.
/// </para>
/// </summary>
public static class UnnecessaryUsings
{
	/// <summary>The compiler's own "unnecessary using directive".</summary>
	private const string UnnecessaryUsingId = "CS8019";

	public static async Task<UsingCleanup> RemoveAsync(
		Solution solution,
		IReadOnlyList<DocumentId> documentIds,
		CancellationToken cancellationToken)
	{
		var removed = new List<string>();

		foreach (var documentId in documentIds)
		{
			var document = solution.GetDocument(documentId);
			if (document is null) continue;

			var directives = await FindUnnecessaryAsync(document, cancellationToken);
			if (directives.Count == 0) continue;

			var text = await document.GetTextAsync(cancellationToken);

			// Whole lines, back to front. A directive removed by span would leave its line ending
			// behind; back to front keeps the earlier spans valid as the later ones go.
			foreach (var directive in directives.OrderByDescending(node => node.SpanStart))
			{
				removed.Add(directive.ToString().Trim());
				text = text.Replace(LineSpanOf(text, directive), string.Empty);
			}

			solution = solution.WithDocumentText(documentId, text);
		}

		return new UsingCleanup
		{
			Solution = solution,
			Removed = removed,
		};
	}

	private static async Task<IReadOnlyList<UsingDirectiveSyntax>> FindUnnecessaryAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var model = await document.GetSemanticModelAsync(cancellationToken);
		if (tree is null || model is null) return [];

		var root = await tree.GetRootAsync(cancellationToken);
		var found = new List<UsingDirectiveSyntax>();

		foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
		{
			if (diagnostic.Id != UnnecessaryUsingId) continue;

			if (root.FindNode(diagnostic.Location.SourceSpan) is not UsingDirectiveSyntax directive) continue;

			// A global using belongs to the whole compilation, so whether this file needs it is not
			// the question being answered here.
			if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) continue;

			found.Add(directive);
		}

		return found;
	}

	/// <summary>The directive's span widened to whole lines, so the line ending goes with it.</summary>
	private static TextSpan LineSpanOf(SourceText text, UsingDirectiveSyntax directive)
	{
		var first = text.Lines.GetLineFromPosition(directive.SpanStart);
		var last = text.Lines.GetLineFromPosition(directive.Span.End);

		return TextSpan.FromBounds(first.Start, last.SpanIncludingLineBreak.End);
	}
}
