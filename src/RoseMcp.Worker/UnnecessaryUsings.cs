using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

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

			solution = solution.WithDocumentText(documentId, TidyHeader(text));
		}

		return new UsingCleanup
		{
			Solution = solution,
			Removed = removed,
		};
	}

	/// <summary>
	/// Closes the gaps the removals left.
	/// <para>
	/// Using directives come in groups separated by blank lines, so deleting a whole group leaves
	/// its separators behind -- at the very top of the file, where they are most obvious and where
	/// an analyzer set to enforce formatting will fail the build over them.
	/// </para>
	/// </summary>
	private static SourceText TidyHeader(SourceText text)
	{
		var doomed = new List<TextSpan>();

		// Starting as though the previous line were blank is what drops the leading ones, which is
		// the case a file left with no usings at all ends up in.
		var previousWasBlank = true;

		foreach (var line in text.Lines.Take(HeaderLength(text)))
		{
			var blank = line.ToString().Trim().Length == 0;

			if (blank && previousWasBlank)
			{
				doomed.Add(line.SpanIncludingLineBreak);
				continue;
			}

			previousWasBlank = blank;
		}

		foreach (var span in doomed.OrderByDescending(span => span.Start))
		{
			text = text.Replace(span, string.Empty);
		}

		return text;
	}

	/// <summary>
	/// How many lines of preamble the file has: directives, comments and the blank lines between
	/// them. Stops at the first line of anything else, so nothing below is touched.
	/// </summary>
	private static int HeaderLength(SourceText text)
	{
		var length = 0;

		foreach (var line in text.Lines)
		{
			var trimmed = line.ToString().TrimStart();

			var isPreamble = trimmed.Length == 0
				|| trimmed.StartsWith("//", StringComparison.Ordinal)
				|| trimmed.StartsWith("using ", StringComparison.Ordinal)
				|| trimmed.StartsWith("global using ", StringComparison.Ordinal)
				|| trimmed.StartsWith("extern alias ", StringComparison.Ordinal);

			if (!isPreamble) break;

			length++;
		}

		return length;
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
