using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Resolves a file position to the symbol it refers to, and describes source locations.</summary>
public static class SymbolLocator
{
	/// <summary>
	/// The symbol at a one-based line and column. Handles both the declaration and any reference,
	/// so a caller can point at a use site without first hunting down where the thing is defined.
	/// </summary>
	public static async Task<(ISymbol Symbol, Document Document)> ResolveAsync(
		Solution solution,
		string filePath,
		int line,
		int column,
		CancellationToken cancellationToken)
	{
		var document = FindDocument(solution, filePath)
			?? throw new ArgumentException($"No document in the solution matches '{filePath}'.");

		var text = await document.GetTextAsync(cancellationToken);
		var position = ToPosition(text, filePath, line, column);

		var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken);
		if (symbol is not null) return (symbol, document);

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var token = root?.FindToken(position);
		var near = token is null ? string.Empty : $" Nearest token: '{token.Value.Text}'.";

		throw new ArgumentException(
			$"No symbol at {Path.GetFileName(filePath)}:{line}:{column}.{near} "
				+ "Point at the identifier itself rather than surrounding punctuation or whitespace.");
	}

	public static Document? FindDocument(Solution solution, string filePath)
	{
		var full = Path.GetFullPath(filePath);

		return solution.Projects
			.SelectMany(project => project.Documents)
			.FirstOrDefault(document => document.FilePath is { Length: > 0 } path
				&& string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Converts a one-based line and column to an absolute offset, complaining precisely rather
	/// than throwing an opaque out-of-range error when the position does not exist.
	/// </summary>
	private static int ToPosition(SourceText text, string filePath, int line, int column)
	{
		if (line < 1 || line > text.Lines.Count)
		{
			throw new ArgumentOutOfRangeException(
				nameof(line),
				$"{Path.GetFileName(filePath)} has {text.Lines.Count} line(s); line {line} does not exist.");
		}

		var textLine = text.Lines[line - 1];
		var offset = Math.Clamp(column - 1, 0, textLine.Span.Length);

		return textLine.Start + offset;
	}

	/// <summary>
	/// The full extent of each of a symbol's declarations, widened upwards over the comments written
	/// for it.
	/// <para>
	/// Widened by line rather than by taking the node's full span, because a full span reaches back
	/// to the previous token and so swallows the blank line above the member -- and the answer a
	/// caller wants is where the declaration starts, not where the one before it ended. Attributes
	/// need no widening: they are part of the declaration's own syntax.
	/// </para>
	/// </summary>
	public static async Task<IReadOnlyList<DeclarationSpan>> SpansOfAsync(
		ISymbol symbol,
		CancellationToken cancellationToken)
	{
		var spans = new List<DeclarationSpan>();

		foreach (var reference in symbol.DeclaringSyntaxReferences)
		{
			var node = await reference.GetSyntaxAsync(cancellationToken);

			// A field or a local declares through a variable declarator, and what a caller means by
			// the declaration is the statement around it -- but only that far, since the member
			// around a local is the whole method.
			var declaration = node.Parent is VariableDeclarationSyntax { Parent: { } owner } ? owner : node;

			var text = await declaration.SyntaxTree.GetTextAsync(cancellationToken);
			var path = declaration.SyntaxTree.FilePath;

			if (string.IsNullOrEmpty(path)) continue;

			spans.Add(new DeclarationSpan
			{
				FilePath = path,
				StartLine = FirstLine(text, declaration) + 1,
				EndLine = text.Lines.GetLineFromPosition(declaration.Span.End).LineNumber + 1,
			});
		}

		return spans;
	}

	/// <summary>
	/// The declaration's own first line, or the first line of the comment block written immediately
	/// above it.
	/// </summary>
	private static int FirstLine(SourceText text, SyntaxNode declaration)
	{
		var first = text.Lines.GetLineFromPosition(declaration.SpanStart).LineNumber;

		while (first > 0)
		{
			var previous = text.Lines[first - 1].ToString().TrimStart();
			if (!previous.StartsWith("//", StringComparison.Ordinal)) break;

			first--;
		}

		return first;
	}

	public static async Task<SourceLocation> DescribeAsync(
		Solution solution,
		Location location,
		CancellationToken cancellationToken)
	{
		var span = location.GetLineSpan();
		var preview = await PreviewAsync(location, cancellationToken);

		return new SourceLocation
		{
			FilePath = span.Path,
			Line = span.StartLinePosition.Line + 1,
			Column = span.StartLinePosition.Character + 1,
			Preview = preview,
			GeneratedHintName = await GeneratedHintNameAsync(solution, location, cancellationToken),
		};
	}

	private static async Task<string?> PreviewAsync(Location location, CancellationToken cancellationToken)
	{
		if (location.SourceTree is null) return null;

		var text = await location.SourceTree.GetTextAsync(cancellationToken);
		var line = text.Lines[location.GetLineSpan().StartLinePosition.Line];

		return line.ToString().Trim();
	}

	/// <summary>
	/// Names the generated document a location sits in, if any. Checked by absence from disk first,
	/// because enumerating generated documents forces every generator in the project to run.
	/// </summary>
	private static async Task<string?> GeneratedHintNameAsync(
		Solution solution,
		Location location,
		CancellationToken cancellationToken)
	{
		var path = location.SourceTree?.FilePath;
		if (string.IsNullOrEmpty(path) || File.Exists(path)) return null;

		foreach (var project in solution.Projects)
		{
			foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
			{
				if (string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase)) return document.HintName;
			}
		}

		return null;
	}
}
