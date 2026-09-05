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
	/// XML, or plain text taken as the summary. Plain text is the ordinary case and asking for tags
	/// around a sentence would make the tool cost more than the editor it replaces.
	/// </param>
	/// <param name="indent">The declaration's own indentation.</param>
	/// <param name="lineEnding">The file's line ending.</param>
	public static SyntaxTriviaList Replace(
		SyntaxTriviaList leading,
		string comment,
		string indent,
		string lineEnding)
	{
		var written = Lines(comment, indent, lineEnding);
		var kept = WithoutDocumentation(leading, lineEnding);

		// The comment goes above everything else that survived, because that is where a declaration
		// puts one: attributes sit between the documentation and the member itself.
		return SyntaxFactory.ParseLeadingTrivia(written + kept);
	}

	/// <summary>
	/// Whether a declaration already carries a documentation comment, which decides whether one is
	/// being replaced or added.
	/// </summary>
	public static bool Present(SyntaxTriviaList leading) =>
		leading.Any(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
			|| trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

	/// <summary>
	/// Checks that XML the caller supplied is well formed, so a malformed tag is refused before the
	/// file is opened rather than landing as CS1570 with a line number in the file.
	/// </summary>
	/// <exception cref="ArgumentException">The text opens a tag it does not close.</exception>
	public static void Guard(string comment)
	{
		if (string.IsNullOrWhiteSpace(comment))
		{
			throw new ArgumentException(
				"No comment was supplied. Pass the summary as plain text, or the whole comment as XML.");
		}

		if (!comment.TrimStart().StartsWith('<')) return;

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
	/// The comment as source lines: each one indented, prefixed and terminated the way the file
	/// does it.
	/// </summary>
	private static string Lines(string comment, string indent, string lineEnding)
	{
		var body = comment.TrimStart().StartsWith('<')
			? comment
			: $"<summary>{comment.Trim()}</summary>";

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
	/// Everything in the leading trivia except the documentation comment and the indentation that
	/// belonged to it.
	/// <para>
	/// A licence header, a region directive or an ordinary comment above the member all mean
	/// something to a reader or to another tool, and none of them is what was asked to change.
	/// </para>
	/// </summary>
	private static string WithoutDocumentation(SyntaxTriviaList leading, string lineEnding)
	{
		var text = leading.ToFullString().Replace("\r\n", "\n", StringComparison.Ordinal);
		var kept = new List<string>();

		foreach (var line in text.Split('\n'))
		{
			if (line.TrimStart().StartsWith(Marker, StringComparison.Ordinal)) continue;

			kept.Add(line);
		}

		// The last entry is the indentation of the declaration itself, which the parser reads as
		// trivia and which has to survive or the member lands at column zero.
		return string.Join(lineEnding, kept);
	}
}
