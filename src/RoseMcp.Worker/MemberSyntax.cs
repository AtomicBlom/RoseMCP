using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Turns supplied C# into member declarations, or refuses before anything is written.
/// <para>
/// The refusing is most of the value. Every mechanical failure a text edit produces -- an
/// unbalanced brace, a dropped access modifier, an escape that leaked into the source, a
/// <c>&lt;/summary&gt;</c> with no opener -- is something the parser sees and a diff does not, and
/// failing here costs nothing. Failing at the next build costs a build, and leaves the file broken
/// until someone pays for it.
/// </para>
/// <para>
/// The code is parsed inside a synthetic container of the same kind as the real one rather than on
/// its own, because a member declaration only means anything in a container. An enum member is not
/// a declaration anywhere else, and parsing a bare snippet as a compilation unit turns
/// <c>void M() { }</c> into a top-level local function and <c>int x = 1;</c> into a statement --
/// both of which parse cleanly and mean something entirely different from what was asked for.
/// </para>
/// </summary>
public static class MemberSyntax
{
	/// <summary>How many errors are worth listing. Past the first few they are usually consequences.</summary>
	private const int Listed = 5;

	/// <summary>Never compiled and never written; only the parser ever sees it.</summary>
	private const string WrapperName = "__RoseMcpContainer";

	/// <summary>The lines the wrapper adds above the code, so errors are reported in the caller's terms.</summary>
	private const int WrapperLines = 2;

	/// <summary>
	/// The members <paramref name="code"/> declares, in the order they were written, re-indented for
	/// a declaration sitting at <paramref name="indent"/> and written with
	/// <paramref name="lineEnding"/>.
	/// </summary>
	/// <param name="code">The declarations as the caller wrote them.</param>
	/// <param name="containerKeyword">The keyword of the container they are going into.</param>
	/// <param name="options">The destination project's parse options.</param>
	/// <param name="indent">The indentation of the declaration they sit beside.</param>
	/// <param name="lineEnding">
	/// The destination file's ending, applied to code whose own endings are all bare LFs -- inside a
	/// literal as well as outside one. An agent composing C# for a JSON argument writes LF without
	/// deciding to, and in a CRLF repository the file that produces fails <c>dotnet format</c> while
	/// no build says anything. Code carrying even one CR LF is left exactly as it arrived, which is
	/// how to ask for a bare LF inside a literal on purpose. Empty leaves every ending untouched.
	/// </param>
	/// <param name="rewritten">
	/// How many endings were changed, so a caller can say so. It is a change to what a literal says,
	/// and it must not be silent.
	/// </param>
	/// <param name="copied">
	/// The leading part of <paramref name="code"/> that came out of the file rather than from the
	/// caller -- the signature, where only a body is being replaced. It is already indented for
	/// where it sits and already carries the file's endings, so it is left alone and the caller's
	/// baseline is read from what follows it. Without that, the signature's indentation is taken as
	/// the body's and a hand-wrapped call inside the body comes out flat against its own statement.
	/// </param>
	/// <exception cref="ArgumentException">
	/// The code does not parse, declares no member, or would put something outside the container.
	/// </exception>
	public static IReadOnlyList<MemberDeclarationSyntax> Parse(
		string code,
		string containerKeyword,
		ParseOptions? options,
		string indent = "",
		string lineEnding = "",
		Action<int>? rewritten = null,
		string copied = "")
	{
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("No code was supplied, so there is nothing to write.");

		var members = ParseWrapped(code, containerKeyword, options);
		if (indent.Length == 0 && lineEnding.Length == 0) return members;

		// Parsed twice, because the indentation cannot be worked out until the code has been
		// understood: which lines sit inside a multi-line literal decides which of them have to be
		// left exactly as they arrived. The second parse is of text, in microseconds, against an edit
		// that is about to compile a project.
		var shifted = Shift(code, indent, Literals(members), lineEnding, rewritten, Copied(code, copied));

		return string.Equals(shifted, code, StringComparison.Ordinal)
			? members
			: ParseWrapped(shifted, containerKeyword, options);
	}

	/// <summary>
	/// The keyword a container was declared with, so the synthetic one parses by the same rules as
	/// the real one.
	/// </summary>
	public static string KeywordOf(BaseTypeDeclarationSyntax declaration) => declaration switch
	{
		RecordDeclarationSyntax record when !record.ClassOrStructKeyword.IsKind(SyntaxKind.None) =>
			$"{record.Keyword.Text} {record.ClassOrStructKeyword.Text}",
		TypeDeclarationSyntax type => type.Keyword.Text,
		EnumDeclarationSyntax => "enum",
		_ => "class",
	};

	/// <summary>
	/// The parameters <paramref name="text"/> declares, taken as what goes between the parentheses,
	/// with any lines it wraps onto indented for a declaration sitting at <paramref name="indent"/>.
	/// <para>
	/// Source text rather than a structured list, because it is what someone writing C# already
	/// knows how to write, and it carries for free everything a structured shape would have to
	/// enumerate: defaults, ref and out, params, attributes, nullable annotations, generic arguments.
	/// It also puts this behind the same promise as everything else here -- if it does not parse,
	/// nothing is written.
	/// </para>
	/// <para>
	/// The indentation is added rather than replaced, unlike a member's, because every line of a
	/// parameter list is a continuation: there is no first line at column zero to take a baseline
	/// from, so what the caller writes is read as relative to the declaration and the declaration's
	/// own indentation goes in front of it.
	/// </para>
	/// </summary>
	public static SeparatedSyntaxList<ParameterSyntax> ParseParameters(
		string text,
		ParseOptions? options,
		string indent = "")
	{
		var list = SyntaxFactory.ParseParameterList($"({ShiftContinuations(text, indent)})", options: options);

		var errors = list.GetDiagnostics()
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.ToArray();

		// One column for the parenthesis this added, and no lines at all: a parameter list is not
		// wrapped in anything, so the only offset is the one character.
		if (errors.Length > 0) throw Rejected(text, errors, lineOffset: 0, columnOffset: 1);

		return list.Parameters;
	}

	/// <summary>
	/// A fragment re-indented for where it is going: the baseline it was written at taken off every
	/// line, <paramref name="indent"/> put on.
	/// <para>
	/// The rule a whole member goes through, on something smaller than one. A caller that has read the
	/// file and indented for the destination and a caller that wrote at column zero are the same
	/// request, and only taking the baseline off first makes them so -- otherwise the two indentations
	/// add up and the code lands as deep as the caller's own habits made it.
	/// </para>
	/// <para>
	/// The first line keeps neither, because it is spliced at a point that already carries the
	/// indentation of the line it lands on. The lines of a multi-line literal keep both: leading
	/// whitespace there is the value in a verbatim literal and decides how much is stripped from a raw
	/// one, so moving one such line and not another changes what the program says rather than how it
	/// reads. Found here rather than asked of the caller, since a caller who forgets does not find out.
	/// </para>
	/// </summary>
	/// <param name="code">The fragment as the caller wrote it.</param>
	/// <param name="indent">The indentation of the code it is going beside.</param>
	public static string Reindented(string code, string indent)
	{
		var lines = Split(code);
		var baseline = Baseline([.. lines.Select(line => line.Content)]);

		if (baseline.Length == 0 && indent.Length == 0) return code;

		var untouched = LiteralLines(code);

		// Which line is the first is asked of the content rather than of the index, because a fragment
		// that opens with a line break has its baseline on the second line and belongs at the splice
		// point all the same. Indenting it as a continuation puts the whole fragment a level out.
		var first = lines.ToList().FindIndex(line => line.Content.Trim().Length > 0);

		var shifted = lines.Select((line, index) =>
		{
			if (untouched.Contains(index)) return line.Content + line.Ending;

			var stripped = baseline.Length > 0 && line.Content.StartsWith(baseline, StringComparison.Ordinal)
				? line.Content[baseline.Length..]
				: line.Content;

			// Padding a blank line only makes trailing whitespace for the next pass to strip again.
			var prefixed = index > first && stripped.Trim().Length > 0 ? indent + stripped : stripped;

			return prefixed + line.Ending;
		});

		return string.Concat(shifted);
	}

	/// <summary>True for a comment of any kind, documentation included.</summary>
	public static bool IsComment(SyntaxTrivia trivia) => trivia.Kind() is
		SyntaxKind.SingleLineCommentTrivia
			or SyntaxKind.MultiLineCommentTrivia
			or SyntaxKind.SingleLineDocumentationCommentTrivia
			or SyntaxKind.MultiLineDocumentationCommentTrivia;

	/// <summary>
	/// Refuses a comment written after the last member. It attaches to the container's closing brace
	/// rather than to any member, so it is not part of anything being written and would be dropped
	/// without trace -- and a lost comment is invisible in the diff that reports the change.
	/// </summary>
	private static void GuardDanglingComment(BaseTypeDeclarationSyntax wrapper)
	{
		if (!wrapper.CloseBraceToken.LeadingTrivia.Any(IsComment)) return;

		throw new ArgumentException(
			"The code ends with a comment that belongs to no member, so it would be dropped. Put it above "
				+ "the member it describes.");
	}

	/// <summary>
	/// Parses the code inside a synthetic container and checks the shape of what came out.
	/// </summary>
	private static IReadOnlyList<MemberDeclarationSyntax> ParseWrapped(
		string code,
		string containerKeyword,
		ParseOptions? options)
	{
		var wrapped = $"{containerKeyword} {WrapperName}\n{{\n{code.TrimEnd()}\n}}\n";
		var tree = CSharpSyntaxTree.ParseText(wrapped, options as CSharpParseOptions);

		var errors = tree.GetDiagnostics()
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.ToArray();

		if (errors.Length > 0) throw Rejected(code, errors);

		// One container and nothing beside it. A brace closed once too often parses without any
		// error at all by ending the wrapper early, which would otherwise carry whatever follows
		// past every check here and land it in the file at top level.
		if (((CompilationUnitSyntax)tree.GetRoot()).Members is not [BaseTypeDeclarationSyntax wrapper]
			|| wrapper.Identifier.Text != WrapperName)
		{
			throw new ArgumentException(
				"The code closes more braces than it opens, so part of it would end up outside the member. "
					+ "Supply the member declarations alone.");
		}

		GuardDanglingComment(wrapper);

		var members = Members(wrapper);
		if (members.Count == 0) throw new ArgumentException("The code declares no member, so there is nothing to write.");

		return members;
	}

	/// <summary>
	/// Re-indents code for where it is going: its own baseline indentation off every line, then the
	/// destination's on.
	/// <para>
	/// This is the half of the promise the formatter cannot keep. It reindents statements and moves
	/// braces, which are rules it has, and a line wrapped by hand inside a body comes out right
	/// because of them -- but a wrapped parameter list is layout it has no rule about, so it keeps
	/// whatever indentation arrived. Code written for column zero then lands a level short of its
	/// neighbours, and neither IDE0055 nor dotnet format says a word, because neither of them has an
	/// opinion either. Measured on this repository's own source, writing a member through this tool.
	/// </para>
	/// <para>
	/// Both halves are needed rather than just the shift: a caller that has read the file and
	/// indented for the destination is as likely as one that wrote at column zero, and only removing
	/// the baseline first makes the two the same request.
	/// </para>
	/// <para>
	/// The first <paramref name="copied"/> lines are exempt from all of it, and that exemption is
	/// what makes replacing a body safe. A body replacement composes a signature copied out of the
	/// file with a body the caller wrote, and the two arrive in different coordinate systems: the
	/// signature is already indented for where it sits, while the body carries whatever baseline the
	/// caller happened to write it at. One baseline taken off both strips a level from every line
	/// the caller wrapped by hand and nothing from the lines they did not, which lands a wrapped
	/// call flat against its own statement. Nothing downstream notices: a continuation line is not a
	/// statement, so the formatter has no rule that puts it back, and no analyzer has an opinion
	/// about where a wrapped argument list sits.
	/// </para>
	/// <para>
	/// A verbatim literal's interior is never moved: its whitespace is its value, and there is no
	/// delimiter rule to take it back out again. A raw literal's is moved with everything else,
	/// because its value is what remains once the closing delimiter's indentation has been stripped
	/// from every line -- so shifting the content and the delimiter by the same amount leaves the
	/// value identical while putting the literal at the indentation of the code around it.
	/// </para>
	/// <para>
	/// Endings are rewritten to <paramref name="lineEnding"/>, literals included, but only when
	/// every ending the caller wrote is a bare LF -- the copied lines are not asked, since their
	/// endings came out of the file and would answer on behalf of a caller who said nothing. That is
	/// a change to what a string says, so what licenses it is the caller having said nothing about
	/// endings at all: an agent composing C# for a tool argument writes LF without deciding to, and
	/// the file that produces fails a formatting check in a CRLF repository while no build reports
	/// anything. A single CR LF anywhere in the code says the caller is thinking about endings, and
	/// then every one of them is left exactly as it arrived -- which is also how to ask for a bare
	/// LF inside a literal deliberately.
	/// </para>
	/// </summary>
	private static string Shift(
		string code,
		string indent,
		Literal literals,
		string lineEnding,
		Action<int>? rewritten,
		int copied)
	{
		var lines = Split(code);
		var written = lines.Skip(copied).ToArray();
		var baseline = Baseline([.. written.Select(line => line.Content)]);
		var changed = 0;

		var wanted = written.All(line => line.Ending is "" or "\n") ? lineEnding : string.Empty;

		var shifted = lines.Select((line, index) =>
		{
			// A copied line is already where it belongs and carries the file's own ending, so both
			// halves of this pass would only move it away from its neighbours.
			if (index < copied) return line.Content + line.Ending;

			var ending = Ending(line.Ending, wanted, ref changed);

			if (literals.Verbatim.Contains(index)) return line.Content + ending;

			var stripped = baseline.Length > 0 && line.Content.StartsWith(baseline, StringComparison.Ordinal)
				? line.Content[baseline.Length..]
				: line.Content;

			// The first line's indentation comes from the trivia at the splice point, and padding a
			// blank line only creates trailing whitespace for the next pass to strip again. A raw
			// literal's own blank line is padded, because there it is content and the delimiter's
			// indentation is about to be taken back off it.
			var content = literals.Raw.Contains(index) || stripped.Trim().Length > 0;
			var prefixed = index > 0 && content ? indent + stripped : stripped;

			return prefixed + ending;
		});

		var result = string.Concat(shifted);

		if (changed > 0) rewritten?.Invoke(changed);

		return result;
	}

	/// <summary>
	/// The ending a line comes out with. A lone LF takes the destination's; anything else is left,
	/// so a caller that wrote a CR LF pair keeps it even in a file that is otherwise LF.
	/// </summary>
	private static string Ending(string arrived, string wanted, ref int changed)
	{
		if (wanted.Length == 0 || arrived != "\n" || wanted == "\n") return arrived;

		changed++;

		return wanted;
	}

	/// <summary>The indentation the code was written at, taken from its first line with content.</summary>
	private static string Baseline(IReadOnlyList<string> lines)
	{
		foreach (var line in lines)
		{
			if (line.Trim().Length == 0) continue;

			return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
		}

		return string.Empty;
	}

	/// <summary>
	/// How many lines at the top of the code came out of the file rather than from the caller.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// The code does not begin with what was said to have been copied out of the file, so the count
	/// would exempt the wrong lines and move ones the file had already placed.
	/// </exception>
	private static int Copied(string code, string copied)
	{
		if (copied.Length == 0) return 0;

		if (!code.StartsWith(copied, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"The code does not begin with the part said to have been copied out of the file.");
		}

		return Split(copied).Count;
	}

	/// <summary>
	/// The lines, each with the ending it actually has. Splitting on a newline and joining with one
	/// would rewrite every CRLF in the code to LF, which inside a string literal is a change to what
	/// the program says rather than to how it looks.
	/// </summary>
	private static IReadOnlyList<(string Content, string Ending)> Split(string code)
	{
		var lines = new List<(string, string)>();
		var start = 0;

		for (var index = 0; index < code.Length; index++)
		{
			if (code[index] is not ('\n' or '\r')) continue;

			var ending = code[index] == '\r' && index + 1 < code.Length && code[index + 1] == '\n'
				? "\r\n"
				: code[index].ToString();

			lines.Add((code[start..index], ending));

			index += ending.Length - 1;
			start = index + 1;
		}

		// Whatever follows the last ending, which is the final line and has no ending of its own.
		lines.Add((code[start..], string.Empty));

		return lines;
	}

	/// <summary>
	/// Every line but the first with <paramref name="indent"/> in front of it. Blank lines are left
	/// blank, since padding one only makes trailing whitespace for the next pass to strip.
	/// </summary>
	private static string ShiftContinuations(string text, string indent)
	{
		if (indent.Length == 0) return text;

		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		return string.Join(
			"\n",
			lines.Select((line, index) => index == 0 || line.Trim().Length == 0 ? line : indent + line));
	}

	/// <summary>
	/// Lines whose leading whitespace belongs to a string rather than to the layout, told apart by
	/// what the language does with that whitespace.
	/// <para>
	/// In a verbatim literal it is the value, and nothing can take it back out, so those lines are
	/// left exactly where they are. In a raw literal the closing delimiter's indentation is stripped
	/// from every line, so moving the whole literal by one amount is invisible to the value -- and
	/// not moving it leaves a literal written at column zero sitting a level out from the code around
	/// it, which nothing downstream corrects and no analyzer reports.
	/// </para>
	/// </summary>
	private static Literal Literals(IReadOnlyList<MemberDeclarationSyntax> members)
	{
		var verbatim = new HashSet<int>();
		var raw = new HashSet<int>();

		foreach (var member in members)
		{
			foreach (var node in member.DescendantNodesAndSelf())
			{
				if (node is not (LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)) continue;

				var span = node.SyntaxTree.GetLineSpan(node.Span);
				if (span.StartLinePosition.Line == span.EndLinePosition.Line) continue;

				var into = IsRaw(node) ? raw : verbatim;

				// From the line after the opening delimiter through the one carrying the closing one:
				// a raw literal's terminator sets the indentation stripped from the rest, so it moves
				// with them or the value changes.
				for (var line = span.StartLinePosition.Line + 1; line <= span.EndLinePosition.Line; line++)
				{
					into.Add(line - WrapperLines);
				}
			}
		}

		return new Literal(verbatim, raw);
	}

	/// <summary>
	/// Whether a multi-line literal is a raw one, read from the delimiter it opens with rather than
	/// from a syntax flag, because the interpolated and plain forms carry that in different places.
	/// </summary>
	private static bool IsRaw(SyntaxNode node) =>
		node.ToString().TrimStart('$', '@').StartsWith("\"\"\"", StringComparison.Ordinal);

	/// <summary>Which lines of the supplied code sit inside a literal, and of which kind.</summary>
	private readonly record struct Literal(IReadOnlySet<int> Verbatim, IReadOnlySet<int> Raw);

	private static IReadOnlyList<MemberDeclarationSyntax> Members(BaseTypeDeclarationSyntax wrapper) => wrapper switch
	{
		TypeDeclarationSyntax type => type.Members,
		EnumDeclarationSyntax @enum => [.. @enum.Members],
		_ => [],
	};

	/// <summary>
	/// The parse errors, each located in the code the caller sent rather than in the wrapper they
	/// never saw, and quoting the line so the message can be acted on without reading anything back.
	/// </summary>
	private static ArgumentException Rejected(
		string code,
		IReadOnlyList<Diagnostic> errors,
		int lineOffset = WrapperLines,
		int columnOffset = 0)
	{
		var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		var described = errors.Take(Listed).Select(error =>
		{
			var start = error.Location.GetLineSpan().StartLinePosition;
			var line = start.Line - lineOffset;
			var quoted = line >= 0 && line < lines.Length ? lines[line].Trim() : string.Empty;

			return $"line {line + 1}, column {start.Character + 1 - columnOffset}: {error.Id} {error.GetMessage()}"
				+ (quoted.Length > 0 ? $"  ->  {quoted}" : string.Empty);
		});

		var more = errors.Count > Listed ? $" ... and {errors.Count - Listed} more." : string.Empty;

		// A using directive is the one rejection whose cause is not the code but the tool's scope,
		// so it is worth saying rather than leaving as a bare "type expected".
		var hint = code.TrimStart().StartsWith("using ", StringComparison.Ordinal)
			? " This writes a member, not a file: a using directive belongs above the namespace and has to be added separately."
			: string.Empty;

		return new ArgumentException(
			$"The code does not parse, so nothing was written. {string.Join("; ", described)}{more}{hint}");
	}

	/// <summary>
	/// Which lines of the code sit inside a literal spanning more than one of them, counted from zero.
	/// <para>
	/// Leading whitespace there is the value in a verbatim literal and decides how much is stripped
	/// from a raw one, so re-indenting one such line and not another changes what the program says
	/// rather than how it reads, and nothing downstream reports it.
	/// </para>
	/// <para>
	/// A token spanning two lines is a literal by construction -- an identifier, a keyword and a
	/// punctuator each fit on one, and a comment is trivia rather than a token.
	/// </para>
	/// </summary>
	private static IReadOnlySet<int> LiteralLines(string code)
	{
		var lines = new HashSet<int>();

		foreach (var token in SyntaxFactory.ParseTokens(code))
		{
			var start = LineOf(code, token.SpanStart);
			var end = LineOf(code, token.Span.End - 1);

			// From the line after the opening delimiter through the one carrying the closing one: a raw
			// literal's terminator sets the indentation taken off the rest, so it stays with them.
			for (var line = start + 1; line <= end; line++) lines.Add(line);
		}

		return lines;
	}

	/// <summary>Which line an offset falls on, counted from zero, with CR, LF and CR LF all endings.</summary>
	private static int LineOf(string code, int offset)
	{
		var line = 0;

		for (var index = 0; index < offset && index < code.Length; index++)
		{
			if (code[index] is not ('\n' or '\r')) continue;

			line++;

			if (code[index] == '\r' && index + 1 < code.Length && code[index + 1] == '\n') index++;
		}

		return line;
	}
}
