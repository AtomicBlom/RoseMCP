using System.Text;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.Worker;

/// <summary>
/// Builds and replaces a declaration's documentation comment, in the file's own style.
/// <para>
/// The shape being edited is a line, not a syntax tree: the indentation, the <c>///</c> and the
/// line ending all have to come out exactly as the neighbouring lines have them, and the surest way
/// of that is to copy a neighbour. The same reasoning <see cref="ParamTags"/> works from.
/// </para>
/// <para>
/// Written as its own operation because composing a whole declaration to change a sentence is a
/// trade nobody takes: the alternative is an editor, and once the file is open in one the rest of
/// the change goes through it too.
/// </para>
/// </summary>
public static class DocComment
{
	private const string Marker = "///";

	/// <summary>
	/// The leading trivia with the documentation comment replaced by <paramref name="comment"/>.
	/// </summary>
	/// <param name="leading">The declaration's leading trivia, doc comment, attributes and all.</param>
	/// <param name="comment">
	/// XML, or plain text taken as the summary, without the <c>///</c> markers either way: this
	/// writes them, and <see cref="Guard"/> refuses a comment that carries its own. Plain text is the
	/// ordinary case and asking for tags around a sentence would make the tool cost more than the
	/// editor it replaces.
	/// </param>
	/// <param name="indent">The declaration's own indentation.</param>
	/// <param name="lineEnding">The file's line ending.</param>
	public static SyntaxTriviaList Replace(
		SyntaxTriviaList leading,
		string comment,
		string indent,
		string lineEnding)
	{
		var (above, below, old) = Around(leading, lineEnding);
		var written = Lines(comment, indent, lineEnding, old);

		// The new comment goes exactly where the old one was, which is what keeps a blank line above the
		// member above it and a licence header or region directive over the top of both. Writing the
		// comment first and everything else after moves all of that underneath, so the member reads as
		// joined to whatever precedes it and separated from its own documentation.
		return SyntaxFactory.ParseLeadingTrivia(above + written + below);
	}

	/// <summary>
	/// Whether a declaration already carries a documentation comment, which decides whether one is
	/// being replaced or added.
	/// </summary>
	public static bool Present(SyntaxTriviaList leading) =>
		leading.Any(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
			|| trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

	/// <summary>
	/// Checks that the comment can be written as it stands, so text that would come out wrong is refused
	/// before the file is opened rather than landing in it: XML that does not parse is CS1570 with a line
	/// number in the file, a comment copied out of a file with its markers still on would be marked a
	/// second time, every line read as text under a <c>///</c> of its own, and plain text followed by a
	/// param or returns tag would put that tag inside the summary the text is wrapped in.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The text is empty, carries its own <c>///</c> or <c>/**</c> markers, follows plain text with a
	/// top-level documentation tag, or opens a tag it does not close.
	/// </exception>
	public static void Guard(string comment)
	{
		if (string.IsNullOrWhiteSpace(comment))
		{
			throw new ArgumentException(
				"No comment was supplied. Pass the summary as plain text, or the whole comment as XML.");
		}

		if (CarriesMarkers(comment))
		{
			throw new ArgumentException(
				"The comment carries its own /// or /** markers, and every line of it would be written behind a "
					+ "second ///. Pass the summary as plain text, or the whole comment as XML without the markers.");
		}

		var isXml = comment.TrimStart().StartsWith('<');
		var misplaced = isXml ? null : TopLevelTag(comment);

		if (misplaced is not null)
		{
			throw new ArgumentException(
				$"The comment starts as plain text and carries a <{misplaced}> tag further on. Plain text is "
					+ "wrapped in a summary of its own, so the tag would be nested inside it. Pass the whole comment "
					+ "as XML starting with <summary>, or the summary as plain text without the tag.");
		}

		if (!isXml) return;

		try
		{
			// Wrapped, because a documentation comment is a list of elements rather than a document
			// with one root, and XDocument insists on the latter.
			XDocument.Parse($"<doc>{comment}</doc>");
		}
		catch (System.Xml.XmlException error)
		{
			throw new ArgumentException(
				$"The comment is not well-formed XML: {error.Message} A documentation comment that does not "
					+ "parse is CS1570, which is a build error where the analyzers are turned up.");
		}
	}

	/// <summary>
	/// Whether any line of the comment starts with a documentation comment's own marker, the way it reads
	/// copied out of a file. Only the start of a line counts, so a path or a URL in the prose does not.
	/// </summary>
	private static bool CarriesMarkers(string comment) =>
		comment.Split('\n')
			.Select(line => line.TrimStart())
			.Any(line => line.StartsWith(Marker, StringComparison.Ordinal)
				|| line.StartsWith("/**", StringComparison.Ordinal));

	/// <summary>
	/// The documentation tags that stand beside a summary rather than inside one. Plain text followed by
	/// one of these is a whole comment whose summary was left untagged.
	/// </summary>
	private static readonly string[] TopLevelTags =
	[
		"summary", "remarks", "param", "typeparam", "returns", "value", "exception", "example", "seealso",
		"inheritdoc", "include", "permission",
	];

	/// <summary>
	/// The name of a top-level tag in text that is otherwise plain, opening or closing: one starting a
	/// line, or a summary tag anywhere. Inline tags such as see, c, paramref and para belong inside a
	/// summary and are not counted, so prose that names a member is still plain text.
	/// </summary>
	private static string? TopLevelTag(string comment)
	{
		var namesSummary = comment.Contains("<summary", StringComparison.Ordinal)
			|| comment.Contains("</summary", StringComparison.Ordinal);

		if (namesSummary) return "summary";

		return comment.Split('\n')
			.Select(line => TagName(line.TrimStart()))
			.FirstOrDefault(name => name is not null && TopLevelTags.Contains(name));
	}

	/// <summary>
	/// The name of the tag a line opens with, opening or closing, or null when it does not open with one.
	/// The whole name is read, so paramref is not taken for param.
	/// </summary>
	private static string? TagName(string line)
	{
		if (!line.StartsWith('<')) return null;

		var name = new string([.. line.Skip(1).SkipWhile(character => character == '/').TakeWhile(char.IsLetter)]);

		return name.Length == 0 ? null : name;
	}

	/// <summary>
	/// The comment as source lines: each one indented, prefixed and terminated the way the file
	/// does it.
	/// </summary>
	private static string Lines(string comment, string indent, string lineEnding, IReadOnlyList<string> old)
	{
		var body = comment.TrimStart().StartsWith('<')
			? comment
			: Summary(comment, indent, old);

		var written = new StringBuilder();

		foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			var trimmed = line.TrimEnd();

			// A blank line inside a comment is still a comment line, or the declaration below it stops
			// being documented at all and every tag after the gap is a separate comment.
			written.Append(indent).Append(Marker);

			if (trimmed.Trim().Length > 0) written.Append(' ').Append(trimmed.TrimStart());

			written.Append(lineEnding);
		}

		return written.ToString();
	}

	/// <summary>
	/// Plain text as a <c>&lt;summary&gt;</c>, laid out the way the repository writes one.
	/// <para>
	/// A sentence short enough for one line goes on one, tags and all. Anything else gets the tags on
	/// lines of their own and its text wrapped beneath them: text the caller broke over several lines,
	/// text too long for one, and a summary replacing one that was written that way -- because a rewrite
	/// that turns a block back into one long line is the change to the comment's shape nobody asked for.
	/// The caller's own line breaks are kept, and a line is only ever broken where it is too long.
	/// </para>
	/// </summary>
	private static string Summary(string comment, string indent, IReadOnlyList<string> old)
	{
		var lines = comment.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Split('\n')
			.Select(line => line.Trim())
			.SkipWhile(line => line.Length == 0)
			.Reverse()
			.SkipWhile(line => line.Length == 0)
			.Reverse()
			.ToArray();

		var width = Math.Max(Width, old.Count == 0 ? 0 : old.Max(Columns));
		var room = width - Columns(indent + Marker + " ");
		var single = lines.Length == 1 ? $"<summary>{lines[0]}</summary>" : null;

		var block = single is null
			|| old.Any(line => line.Trim() == $"{Marker} <summary>")
			|| single.Length > room;

		if (!block) return single!;

		var wrapped = lines.SelectMany(line => line.Length == 0 ? [string.Empty] : Wrapped(line, room));

		return $"<summary>\n{string.Join("\n", wrapped)}\n</summary>";
	}

	/// <summary>
	/// The widest a documentation line runs when nothing says otherwise, in columns with a tab as four:
	/// where this repository's own comments wrap. A comment being replaced that ran wider sets its own.
	/// </summary>
	private const int Width = 104;

	/// <summary>How many columns some text takes, with a tab as four.</summary>
	private static int Columns(string text) => text.Sum(character => character == '\t' ? 4 : 1);

	/// <summary>A line broken between words wherever it would run past <paramref name="room"/>.</summary>
	private static IEnumerable<string> Wrapped(string line, int room)
	{
		var current = new StringBuilder();

		foreach (var word in Words(line))
		{
			if (current.Length > 0 && current.Length + 1 + word.Length > room)
			{
				yield return current.ToString();
				current.Clear();
			}

			if (current.Length > 0) current.Append(' ');

			current.Append(word);
		}

		if (current.Length > 0) yield return current.ToString();
	}

	/// <summary>
	/// The words of a line, a tag counted as one however many spaces it holds: breaking
	/// <c>&lt;see cref="X"/&gt;</c> after "see" leaves a tag split over two comment lines.
	/// </summary>
	private static IEnumerable<string> Words(string line)
	{
		var word = new StringBuilder();
		var depth = 0;

		foreach (var character in line)
		{
			if (character == '<') depth++;
			else if (character == '>' && depth > 0) depth--;

			if (character == ' ' && depth == 0)
			{
				if (word.Length > 0) yield return word.ToString();

				word.Clear();
				continue;
			}

			word.Append(character);
		}

		if (word.Length > 0) yield return word.ToString();
	}

	/// <summary>
	/// The leading trivia either side of the documentation comment, as text, so a new one can go
	/// exactly where the old one was, and the old comment's own lines, which say how it was laid out.
	/// <para>
	/// Everything above stays above and everything below stays below. A licence header, a region
	/// directive or an ordinary comment above the member all mean something to a reader or to another
	/// tool, and so does the blank line that separates the member from the one before it -- none of
	/// them is what was asked to change, and moving any of them under the new comment leaves the
	/// member joined to what precedes it and separated from its own documentation.
	/// </para>
	/// <para>
	/// With no comment to replace, the split is immediately above the declaration's own indentation.
	/// That last trivia line is what the parser reads the declaration's column from, so it has to stay
	/// below or the member lands at column zero.
	/// </para>
	/// </summary>
	private static (string Above, string Below, IReadOnlyList<string> Old) Around(SyntaxTriviaList leading, string lineEnding)
	{
		var lines = leading.ToFullString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		var first = Array.FindIndex(lines, IsDocumentation);
		var last = Array.FindLastIndex(lines, IsDocumentation);

		string[] old = first < 0 ? [] : lines[first..(last + 1)];

		if (first < 0) (first, last) = (lines.Length - 1, lines.Length - 2);

		var above = first == 0 ? string.Empty : string.Join(lineEnding, lines.Take(first)) + lineEnding;

		return (above, string.Join(lineEnding, lines.Skip(last + 1)), old);
	}

	/// <summary>One line of a documentation comment, told from any other trivia line by its marker.</summary>
	private static bool IsDocumentation(string line) =>
		line.TrimStart().StartsWith(Marker, StringComparison.Ordinal);
}
