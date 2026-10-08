using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RoseMcp.Worker;

/// <summary>
/// A documentation comment's summary as one line of prose, with every reference rendered as the name
/// it points at.
/// <para>
/// The names are kept because they are usually the load-bearing part of the sentence: a summary reading
/// "goes after <c>Report</c>, which is the shared part" says something, and the same sentence with the
/// reference dropped -- which is what concatenating the text nodes gives, since a
/// <c>&lt;see cref/&gt;</c> carries its target in an attribute -- has a hole where the subject was.
/// </para>
/// </summary>
public static partial class DocumentationText
{
	/// <summary>
	/// The <c>&lt;summary&gt;</c> of <paramref name="xml"/>, rendered and flattened to one line, or null
	/// when there is none, it renders to nothing, or the XML does not parse.
	/// </summary>
	/// <param name="xml">Documentation XML as <c>ISymbol.GetDocumentationCommentXml</c> returns it.</param>
	public static string? Summary(string? xml)
	{
		if (string.IsNullOrWhiteSpace(xml)) return null;

		XElement? summary;
		try
		{
			summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
		}
		catch (XmlException)
		{
			// Malformed documentation is the author's problem and not the reader's: an outline that
			// throws over a comment answers nothing about the members the caller asked for.
			return null;
		}

		if (summary is null) return null;

		var builder = new StringBuilder();
		AppendContent(builder, summary);

		var text = Whitespace().Replace(builder.ToString(), " ").Trim();
		return text.Length == 0 ? null : text;
	}

	/// <summary>
	/// The short name a documentation ID names, the way a reader would write it in a sentence:
	/// <c>T:Ns.Outer`1</c> is <c>Outer</c>, <c>M:Ns.Type.Report(System.String)</c> is <c>Report</c>, a
	/// constructor is its type's name, and an explicit interface implementation is the member's own name.
	/// A reference the compiler could not resolve arrives as <c>!:</c> and the text the author wrote,
	/// which is shortened the same way.
	/// </summary>
	/// <param name="cref">The <c>cref</c> attribute's value.</param>
	public static string CrefName(string cref)
	{
		var id = cref.Trim();
		var hasPrefix = id.Length >= 2 && id[1] == ':' && (char.IsAsciiLetterUpper(id[0]) || id[0] == '!');
		if (hasPrefix) id = id[2..];

		// The parameter list and a conversion operator's return type are not part of the name, and a
		// generic argument list -- {T} in an ID, <T> in the source text of an unresolved one -- is not
		// part of the short name.
		var tail = id.IndexOfAny(['(', '~']);
		if (tail >= 0) id = id[..tail];

		id = WithoutGroups(id, '{', '}');
		id = WithoutGroups(id, '<', '>');

		var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0) return cref;

		var last = WithoutArity(segments[^1]);
		var isConstructor = last is "#ctor" or "#cctor";
		if (isConstructor)
		{
			return segments.Length > 1 ? WithoutArity(segments[^2]) : last;
		}

		// An explicit interface implementation's name is the interface's, with its dots turned into #:
		// Ns#IFoo#Bar. The member's own name is the part after the last one.
		var hash = last.LastIndexOf('#');
		if (hash >= 0 && hash < last.Length - 1) last = last[(hash + 1)..];

		return last;
	}

	private static void AppendContent(StringBuilder builder, XElement element)
	{
		foreach (var node in element.Nodes())
		{
			switch (node)
			{
				case XText text:
					builder.Append(text.Value);
					break;
				case XElement child:
					AppendElement(builder, child);
					break;
			}
		}
	}

	private static void AppendElement(StringBuilder builder, XElement element)
	{
		switch (element.Name.LocalName)
		{
			case "see" or "seealso":
				AppendReference(builder, element);
				break;
			case "paramref" or "typeparamref":
				builder.Append((string?)element.Attribute("name"));
				break;
			case "para" or "list" or "listheader" or "item" or "term" or "description" or "br":
				// Block elements sit between sentences, so their edges are word breaks the whitespace
				// collapse turns into one space rather than run two sentences together.
				builder.Append(' ');
				AppendContent(builder, element);
				builder.Append(' ');
				break;
			default:
				AppendContent(builder, element);
				break;
		}
	}

	/// <summary>
	/// A <c>&lt;see&gt;</c>: the text the author put inside it when there is any, since that is the
	/// wording they chose, and otherwise the name, keyword or address it points at.
	/// </summary>
	private static void AppendReference(StringBuilder builder, XElement element)
	{
		var hasText = element.Nodes().Any(node => node is XElement || node is XText text && !string.IsNullOrWhiteSpace(text.Value));
		if (hasText)
		{
			AppendContent(builder, element);
			return;
		}

		var cref = (string?)element.Attribute("cref");
		if (!string.IsNullOrWhiteSpace(cref))
		{
			builder.Append(CrefName(cref));
			return;
		}

		builder.Append((string?)element.Attribute("langword") ?? (string?)element.Attribute("href"));
	}

	private static string WithoutGroups(string text, char open, char close)
	{
		if (text.IndexOf(open) < 0) return text;

		var builder = new StringBuilder(text.Length);
		var depth = 0;
		foreach (var character in text)
		{
			if (character == open)
			{
				depth++;
			}
			else if (character == close && depth > 0)
			{
				depth--;
			}
			else if (depth == 0)
			{
				builder.Append(character);
			}
		}

		return builder.ToString();
	}

	/// <summary>A generic type's arity in an ID, <c>`1</c>, or a generic method's, <c>``1</c>.</summary>
	private static string WithoutArity(string segment)
	{
		var tick = segment.IndexOf('`');
		return tick > 0 ? segment[..tick] : segment;
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
