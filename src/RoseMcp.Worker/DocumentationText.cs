using System.Text;
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
public static class DocumentationText
{
	/// <summary>
	/// The longest first sentence <see cref="FirstSentence"/> gives. A summary is one sentence by
	/// convention and not by rule, and one written as a single run-on paragraph would otherwise put the
	/// whole paragraph on every member of an outline.
	/// </summary>
	public const int MaxSentence = 300;

	/// <summary>
	/// The <c>&lt;summary&gt;</c> of <paramref name="xml"/>, rendered and flattened to one line, or null
	/// when there is none, it renders to nothing, or the XML does not parse.
	/// </summary>
	/// <param name="xml">Documentation XML as <c>ISymbol.GetDocumentationCommentXml</c> returns it.</param>
	public static string? Summary(string? xml) => Render(xml)?.Text;

	/// <summary>
	/// The first sentence of <paramref name="xml"/>'s summary, rendered as <see cref="Summary"/> renders
	/// it, or null where there is no summary.
	/// <para>
	/// A sentence ends at a full stop, question or exclamation mark followed by a space and anything but a
	/// lower-case letter, and at the edge of a paragraph or list, whichever comes first. Never inside a
	/// <c>&lt;c&gt;</c>, <c>&lt;code&gt;</c> or a reference, whose text is a name rather than prose, and
	/// never after an abbreviation such as <c>e.g.</c>, which is a full stop that ends nothing. A number
	/// such as <c>1.0</c> has no space after its point, so it never ends one. Cut to
	/// <see cref="MaxSentence"/> at a word, marked with an ellipsis, where the sentence runs longer.
	/// </para>
	/// </summary>
	/// <param name="xml">Documentation XML as <c>ISymbol.GetDocumentationCommentXml</c> returns it.</param>
	public static string? FirstSentence(string? xml)
	{
		if (Render(xml) is not { } prose) return null;

		var sentence = prose.Text[..prose.FirstSentenceEnd()].TrimEnd();

		return Bounded(sentence, MaxSentence);
	}

	/// <summary>
	/// <paramref name="text"/> where it is no longer than <paramref name="limit"/>, and otherwise cut at the
	/// last sentence that ends within it, or failing one at the last word, with an ellipsis where the cut
	/// falls mid-sentence. A cut at a sentence needs no mark, since what is given reads as a whole.
	/// </summary>
	/// <param name="text">Rendered prose, as <see cref="Summary"/> gives it.</param>
	/// <param name="limit">The most characters to give.</param>
	public static string Bounded(string text, int limit)
	{
		if (text.Length <= limit) return text;

		var sentenceEnd = text.LastIndexOfAny(['.', '!', '?'], limit - 1);
		var endsASentence = sentenceEnd > 0 && sentenceEnd + 1 < text.Length && text[sentenceEnd + 1] == ' ';
		if (endsASentence) return text[..(sentenceEnd + 1)];

		var space = text.LastIndexOf(' ', limit - 1);
		var cut = space > 0 ? space : limit - 1;

		return text[..cut].TrimEnd() + "…";
	}

	/// <summary>
	/// The summary rendered, with which characters came from a name rather than prose and where each
	/// paragraph or list item begins; null where there is no summary or it renders to nothing.
	/// </summary>
	private static Prose? Render(string? xml)
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

		var prose = new Prose();
		AppendContent(prose, summary);
		prose.Finish();

		return prose.Text.Length == 0 ? null : prose;
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

	private static void AppendContent(Prose prose, XElement element)
	{
		foreach (var node in element.Nodes())
		{
			switch (node)
			{
				case XText text:
					prose.Append(text.Value);
					break;
				case XElement child:
					AppendElement(prose, child);
					break;
			}
		}
	}

	private static void AppendElement(Prose prose, XElement element)
	{
		switch (element.Name.LocalName)
		{
			case "see" or "seealso":
				prose.Literal(() => AppendReference(prose, element));
				break;
			case "paramref" or "typeparamref":
				prose.Literal(() => prose.Append((string?)element.Attribute("name")));
				break;
			case "c" or "code":
				prose.Literal(() => AppendContent(prose, element));
				break;
			case "para" or "list" or "listheader" or "item" or "term" or "description" or "br":
				// Block elements sit between sentences, so their edges are word breaks the whitespace
				// collapse turns into one space rather than run two sentences together, and the place a
				// first sentence ends if no full stop ended it sooner.
				prose.Block();
				AppendContent(prose, element);
				prose.Block();
				break;
			default:
				AppendContent(prose, element);
				break;
		}
	}

	/// <summary>
	/// A <c>&lt;see&gt;</c>: the text the author put inside it when there is any, since that is the
	/// wording they chose, and otherwise the name, keyword or address it points at.
	/// </summary>
	private static void AppendReference(Prose prose, XElement element)
	{
		var hasText = element.Nodes().Any(node => node is XElement || node is XText text && !string.IsNullOrWhiteSpace(text.Value));
		if (hasText)
		{
			AppendContent(prose, element);
			return;
		}

		var cref = (string?)element.Attribute("cref");
		if (!string.IsNullOrWhiteSpace(cref))
		{
			prose.Append(CrefName(cref));
			return;
		}

		prose.Append((string?)element.Attribute("langword") ?? (string?)element.Attribute("href"));
	}

	/// <summary>
	/// Rendered summary text as it is written: whitespace collapsed to single spaces as it arrives, with a
	/// record of which characters are a name or code rather than prose, and where each block begins.
	/// <para>
	/// Kept while rendering rather than worked out from the finished string, because by then a full stop
	/// inside <c>&lt;c&gt;Path.GetFileName&lt;/c&gt;</c> and one ending a sentence look the same.
	/// </para>
	/// </summary>
	private sealed class Prose
	{
		private static readonly string[] Abbreviations = ["e.g", "i.e", "etc", "vs", "cf", "viz", "approx", "incl"];

		private readonly StringBuilder _builder = new();
		private readonly List<bool> _literal = [];
		private readonly List<int> _blocks = [];
		private int _depth;

		public string Text { get; private set; } = "";

		public void Append(string? text)
		{
			if (text is null) return;

			foreach (var character in text)
			{
				var isSpace = char.IsWhiteSpace(character);
				var collapses = isSpace && (_builder.Length == 0 || _builder[^1] == ' ');
				if (collapses) continue;

				_builder.Append(isSpace ? ' ' : character);
				_literal.Add(_depth > 0 && !isSpace);
			}
		}

		/// <summary>Writes what <paramref name="write"/> appends as a name or code, which no sentence ends inside.</summary>
		public void Literal(Action write)
		{
			_depth++;
			write();
			_depth--;
		}

		/// <summary>The edge of a paragraph or list entry: a word break, and a place a first sentence may end.</summary>
		public void Block()
		{
			Append(" ");
			if (_builder.Length > 0) _blocks.Add(_builder.Length);
		}

		public void Finish() => Text = _builder.ToString().Trim();

		/// <summary>Where the first sentence ends, as a length of <see cref="Text"/>; the whole text where none ends sooner.</summary>
		public int FirstSentenceEnd()
		{
			// The builder never starts with a space, since collapsing drops one written first, so a position
			// in it is the same position in Text.
			var block = _blocks.Where(edge => edge < Text.Length).DefaultIfEmpty(Text.Length).Min();

			for (var index = 0; index < block; index++)
			{
				if (EndsASentence(index)) return index + 1;
			}

			return block;
		}

		private bool EndsASentence(int index)
		{
			var character = Text[index];
			if (character is not ('.' or '!' or '?')) return false;
			if (_literal[index]) return false;

			var atEnd = index + 1 == Text.Length;
			if (atEnd) return true;

			var followedBySpace = Text[index + 1] == ' ';
			var nextStartsLowerCase = index + 2 < Text.Length && char.IsLower(Text[index + 2]);
			if (!followedBySpace || nextStartsLowerCase) return false;

			return character != '.' || !IsAbbreviation(index);
		}

		/// <summary>Whether the word the full stop at <paramref name="index"/> ends is an abbreviation.</summary>
		private bool IsAbbreviation(int index)
		{
			var start = Text.LastIndexOf(' ', index) + 1;
			var word = Text[start..index].TrimStart('(');

			return Abbreviations.Contains(word, StringComparer.OrdinalIgnoreCase);
		}
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
}
