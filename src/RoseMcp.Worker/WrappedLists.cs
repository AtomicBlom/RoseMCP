using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// Finds wrapped lists whose items begin their lines at different depths: arguments, parameters,
/// collection elements and the like, laid out one or more to a line.
/// <para>
/// A continuation line is layout neither Roslyn's formatter nor IDE0055 has a rule about, so a list
/// whose items were written two levels deep beside neighbours one level deep formats clean and passes
/// <c>dotnet format</c>. Whether a list should sit one level or two below the line that opens it is a
/// convention a repository is free to choose, so depth on its own proves nothing. Items of one list
/// disagreeing with each other is a different matter: nobody indents the third argument of a call
/// deeper than the second on purpose, and it is the shape a splice leaves when it adds the
/// destination's indentation to code that already carried it.
/// </para>
/// <para>
/// Reported, never rewritten, because which of the depths was meant is exactly what cannot be told.
/// A list holding a preprocessor directive is passed over, since its branches may be laid out for
/// different builds.
/// </para>
/// </summary>
public static class WrappedLists
{
	/// <summary>
	/// The one-based lines on which an item of a wrapped list begins at a depth the rest of its list
	/// does not use, in order. The depth most of the list's line-beginning items share is taken as
	/// meant, the first of them where no depth has a majority.
	/// </summary>
	/// <param name="root">The file's syntax root.</param>
	/// <param name="text">The file's text, so lines are numbered as they read.</param>
	/// <param name="tabSize">How many columns a tab advances to, so a tab and its spaces compare equal.</param>
	public static IReadOnlyList<int> Disagreeing(SyntaxNode root, SourceText text, int tabSize)
	{
		var lines = new SortedSet<int>();

		foreach (var node in root.DescendantNodes())
		{
			if (ItemsOf(node) is not { } items) continue;
			if (node.ContainsDirectives) continue;

			var starts = items
				.Select(item => Start(item, text, tabSize))
				.OfType<(int Line, int Column)>()
				.ToList();

			if (starts.Count < 2) continue;

			var meant = starts
				.GroupBy(start => start.Column)
				.OrderByDescending(group => group.Count())
				.ThenBy(group => starts.FindIndex(start => start.Column == group.Key))
				.First()
				.Key;

			foreach (var (line, column) in starts)
			{
				if (column != meant) lines.Add(line + 1);
			}
		}

		return [.. lines];
	}

	/// <summary>
	/// One sentence about a file's wrapped lists whose items disagree about their depth, or null where it
	/// has none.
	/// </summary>
	/// <param name="root">The file's syntax root, as it now stands.</param>
	/// <param name="text">The file's text, so lines are numbered as they will read.</param>
	/// <param name="tabSize">How many columns a tab advances to.</param>
	/// <param name="name">The file's name, for a caller holding several results.</param>
	public static string? Notice(SyntaxNode root, SourceText text, int tabSize, string name)
	{
		var lines = Disagreeing(root, text, tabSize);

		if (lines.Count == 0) return null;

		var where = lines.Count == 1
			? $"line {lines[0]} begins an item of a wrapped list at a depth the rest of its list does not use"
			: $"lines {string.Join(", ", lines)} begin items of wrapped lists at depths the rest of each list does not use";

		return $"{name}: {where}, and nothing changed it -- no formatter rule covers a continuation line, so "
			+ "dotnet format will pass it too. Indent it like the other items of its list.";
	}

	/// <summary>The items of a list a caller can wrap, or null for anything else.</summary>
	private static IEnumerable<SyntaxNode>? ItemsOf(SyntaxNode node) => node switch
	{
		BaseArgumentListSyntax list => list.Arguments,
		AttributeArgumentListSyntax list => list.Arguments,
		BaseParameterListSyntax list => list.Parameters,
		TypeArgumentListSyntax list => list.Arguments,
		TypeParameterListSyntax list => list.Parameters,
		CollectionExpressionSyntax collection => collection.Elements,
		InitializerExpressionSyntax initializer => initializer.Expressions,
		TupleExpressionSyntax tuple => tuple.Arguments,
		AttributeListSyntax list => list.Attributes,
		_ => null,
	};

	/// <summary>
	/// The zero-based line and the column an item begins at, or null where something other than
	/// whitespace stands in front of it on its line.
	/// </summary>
	private static (int Line, int Column)? Start(SyntaxNode item, SourceText text, int tabSize)
	{
		var position = item.GetFirstToken().SpanStart;
		var line = text.Lines.GetLineFromPosition(position);
		var column = 0;

		for (var index = line.Start; index < position; index++)
		{
			column = text[index] switch
			{
				'\t' => column + tabSize - column % tabSize,
				' ' => column + 1,
				_ => -1,
			};

			if (column < 0) return null;
		}

		return (line.LineNumber, column);
	}
}
