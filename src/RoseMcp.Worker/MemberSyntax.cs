using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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
	/// The multi-line literals whose values had endings changed, each on the line of
	/// <paramref name="code"/> it opens on, so a caller can say so: that is a change to what a string
	/// says, and it must not be silent. Called only when there is one. An ending changed anywhere else
	/// is layout, and is not reported.
	/// </param>
	/// <param name="reindented">
	/// How many lines inside a multi-line raw literal moved, for the same reason. The value survives
	/// it -- content and closing delimiter move together, and the delimiter's indentation is what is
	/// stripped from the rest -- but the text inside a string is not the text that was supplied, and
	/// a diff showing the member rewritten around it says nothing about which half changed.
	/// </param>
	/// <param name="copied">
	/// The leading part of <paramref name="code"/> that came out of the file rather than from the
	/// caller -- the signature, where only a body is being replaced. It is already indented for
	/// where it sits and already carries the file's endings, so it is left alone and the caller's
	/// baseline is read from what follows it. Without that, the signature's indentation is taken as
	/// the body's and a hand-wrapped call inside the body comes out flat against its own statement.
	/// </param>
	/// <param name="baseline">
	/// The indentation to take off every written line, where the caller knows it rather than leaving
	/// it to be read from the code.
	/// <para>
	/// Reading it from the code is right when the code is the caller's and wrong when it came out of
	/// the file. Text already sitting where it belongs wants the destination's own indentation off and
	/// back on -- the identity this pass should be for it -- and reading the first written line
	/// instead measures how deep the body sits inside its member, so every line comes out a level
	/// shallower. Roslyn's formatter hides that for a block body, whose statements and braces it has
	/// rules for, and not for an expression body, which is a continuation it has no rule about.
	/// </para>
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
		Action<IReadOnlyList<RewrittenLiteral>>? rewritten = null,
		Action<int>? reindented = null,
		string copied = "",
		string? baseline = null)
	{
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("No code was supplied, so there is nothing to write.");

		var members = ParseWrapped(code, containerKeyword, options);
		if (indent.Length == 0 && lineEnding.Length == 0) return members;

		// Parsed twice, because the indentation cannot be worked out until the code has been
		// understood: which lines sit inside a multi-line literal decides which of them have to be
		// left exactly as they arrived. The second parse is of text, in microseconds, against an edit
		// that is about to compile a project.
		var shifted = Shift(
			code,
			indent,
			Literals(members),
			lineEnding,
			rewritten,
			reindented,
			Copied(code, copied),
			baseline);

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
	/// A member as it will read in the file: indented for where it is going, optionally separated
	/// from its neighbours by a blank line, ending its own line, and marked so the formatter and the
	/// whitespace pass know which lines are new.
	/// <para>
	/// The indentation goes on as leading trivia rather than being left to the formatter, and every
	/// path that inserts a member has to do it. Given a member whose first line is already indented,
	/// the formatter leaves the lines it has no rule about -- a wrapped parameter list -- exactly
	/// where they are, which is where the shift put them. Given one with no leading whitespace it
	/// recomputes the indentation itself, and then the shift and the formatter both apply, and every
	/// wrapped line lands a level too deep. Measured twice, on the two paths that insert: writing this
	/// very method through the tool put its attribute arguments at three tabs, and moving a signature
	/// wrapped two levels in landed it at four.
	/// </para>
	/// <para>
	/// The blank line is added here rather than left to the formatter, which reindents and moves
	/// braces but never inserts one between members -- so a member appended without it lands flush
	/// against the one above.
	/// </para>
	/// </summary>
	public static MemberDeclarationSyntax Prepared(
		MemberDeclarationSyntax member,
		bool blankBefore,
		bool blankAfter,
		string lineEnding,
		string indent,
		SyntaxAnnotation marker)
	{
		var newLine = SyntaxFactory.EndOfLine(lineEnding);

		IEnumerable<SyntaxTrivia> leading = WithoutLeadingBlanks(member.GetLeadingTrivia());

		if (indent.Length > 0) leading = [SyntaxFactory.Whitespace(indent), .. leading];
		if (blankBefore) leading = [newLine, .. leading];

		var trailing = member.GetTrailingTrivia();
		if (trailing.Count == 0 || !trailing.Last().IsKind(SyntaxKind.EndOfLineTrivia)) trailing = trailing.Add(newLine);
		if (blankAfter) trailing = trailing.Add(newLine);

		return member
			.WithLeadingTrivia(leading)
			.WithTrailingTrivia(trailing)
			.WithAdditionalAnnotations(marker);
	}

	/// <summary>
	/// The trivia with the blank lines and indentation at the front of it dropped, so what is left
	/// begins at the first thing the member actually says.
	/// </summary>
	public static IReadOnlyList<SyntaxTrivia> WithoutLeadingBlanks(SyntaxTriviaList trivia) =>
		[
			.. trivia.SkipWhile(candidate =>
				candidate.Kind() is SyntaxKind.WhitespaceTrivia or SyntaxKind.EndOfLineTrivia),
		];

	/// <summary>
	/// Says that endings inside a string literal in the supplied code were changed, naming the lines the
	/// literals are on, because a diff cannot: a terminator is not line content, and inside a literal it
	/// is part of what the string says.
	/// <para>
	/// Only literals are named because only there does the rewrite mean anything. Outside one an ending
	/// is layout, and making it the file's is what the whitespace pass does to every line it writes. A
	/// sentence on every payload that ends a line would say nothing a caller could act on, and would
	/// teach them to skim the channel that carries the notices that matter.
	/// </para>
	/// <para>
	/// Here rather than beside one caller, so every path that writes supplied code says the same
	/// sentence.
	/// </para>
	/// </summary>
	/// <param name="literals">The literals whose values changed, each on its line in the code supplied.</param>
	/// <param name="ending">The name of the ending they were rewritten to.</param>
	public static string RewrittenEndings(IReadOnlyList<RewrittenLiteral> literals, string ending) =>
		$"{Rewrote(literals, ending, "of the code supplied")} Write one CR LF anywhere in the code to keep every "
			+ "ending exactly as it arrived.";

	/// <summary>
	/// Says that endings inside a string literal were changed in a member nobody supplied as code: one
	/// being moved, or the part of a body that came out of the file rather than from the caller.
	/// <para>
	/// A sentence of its own because the caller did not write these lines, so they are named against
	/// the member instead. Whether there is a way to keep the endings depends on the write. A move has
	/// no code argument to write a CR LF into. A body edit does, and the rule that rewrites bare LFs is
	/// asked of the whole body it rebuilds, so one CR LF in what the caller sends leaves the file's own
	/// literals exactly as they were.
	/// </para>
	/// </summary>
	/// <param name="literals">The literals whose values changed, each on its line in the member.</param>
	/// <param name="ending">The name of the ending they were rewritten to.</param>
	/// <param name="codeSupplied">
	/// Whether the write took code from the caller, which is what makes the CR LF escape hatch reach these
	/// literals too.
	/// </param>
	public static string RewrittenMemberEndings(IReadOnlyList<RewrittenLiteral> literals, string ending, bool codeSupplied = false) =>
		Rewrote(literals, ending, "of the member")
			+ (codeSupplied
				? " Write one CR LF anywhere in the code sent to leave every ending in the member as it was."
				: string.Empty);

	/// <summary>
	/// The multi-line literals of <paramref name="text"/> whose values hold any of the line breaks
	/// inside <paramref name="span"/>, each named by the line of the span it first appears on.
	/// <para>
	/// For a span whose every ending has just been rewritten -- a replacement spliced into a body --
	/// this is the part of the rewrite that changed what a string says, and the rest of it was layout.
	/// A literal that opened before the span is named by the span's first line, which is where the
	/// caller's text is inside it.
	/// </para>
	/// </summary>
	/// <param name="text">The text as it will be written, the rewritten span included.</param>
	/// <param name="span">The part of it whose endings were rewritten.</param>
	public static IReadOnlyList<RewrittenLiteral> LiteralsHolding(string text, TextSpan span)
	{
		var within = text.Substring(span.Start, span.Length);
		var found = new List<RewrittenLiteral>();

		foreach (var literal in LiteralValues(text))
		{
			var endings = literal.Breaks.Count(span.Contains);
			if (endings == 0) continue;

			var named = Math.Max(literal.Start, span.Start) - span.Start;

			found.Add(new RewrittenLiteral(LineOf(within, named), endings));
		}

		return found;
	}

	/// <summary>
	/// <paramref name="code"/> with every ending rewritten to <paramref name="lineEnding"/>, string
	/// literals included, where each one is a bare LF -- or exactly as it arrived where any carries a CR.
	/// <para>
	/// For code that is a whole file rather than members: there is no wrapper around it, no signature
	/// copied out of the file and no indentation to take off, so the endings are the only part of its
	/// layout to decide here, and the formatter and the whitespace pass see to the rest. It is the rule
	/// <see cref="Shift"/> applies, asked of the whole payload. A caller composing C# for a JSON argument
	/// writes LF without deciding to, and a literal left holding it fails a formatting check in a CRLF
	/// repository while no build reports anything; one CR anywhere says the caller is thinking about
	/// endings, and then none is touched, which is also how to keep a bare LF inside a literal.
	/// </para>
	/// <para>
	/// Every multi-line literal whose value held an ending is reported, by its line in
	/// <paramref name="code"/>, since rewriting is all or nothing and so each of those endings changed.
	/// A literal in every branch of an <c>#if</c> is counted, because each one is rewritten. Asked of the
	/// code before anything is put around it, the lines are the caller's own.
	/// </para>
	/// </summary>
	/// <param name="code">The code as the caller sent it.</param>
	/// <param name="lineEnding">The ending the destination file uses.</param>
	/// <param name="rewritten">Told the literals whose values changed, where any did.</param>
	public static string WithEndings(string code, string lineEnding, Action<IReadOnlyList<RewrittenLiteral>>? rewritten)
	{
		var saysSomething = code.Contains('\r', StringComparison.Ordinal);
		if (saysSomething || lineEnding == "\n") return code;

		RewrittenLiteral[] changed =
		[
			.. LiteralValues(code).Select(value => new RewrittenLiteral(LineOf(code, value.Start), value.Breaks.Count)),
		];

		var ended = code.Replace("\n", lineEnding, StringComparison.Ordinal);

		if (changed.Length > 0) rewritten?.Invoke(changed);

		return ended;
	}

	/// <summary>
	/// The line of <paramref name="into"/> each literal named in <paramref name="from"/> is found on,
	/// unchanged, or null for one that <paramref name="into"/> does not hold as it is.
	/// <para>
	/// For a body rebuilt out of the file's own text with the caller's spliced in. What is parsed joins
	/// the body to the signature on one line, and an insertion joins the statements around it again, so
	/// a line counted there is not one anybody can find in the member. A literal the file already held
	/// is the same text in both, which is what finds it; one the caller's text edited is not, and that
	/// is what tells the two apart.
	/// </para>
	/// <para>
	/// The same literal written twice is told apart by order, and only among the copies on each side
	/// that the other side also holds. A copy the caller wrote has no counterpart in the member, and one
	/// their text replaced has none in what was parsed, so counting either would pair every later copy
	/// with the one before it.
	/// </para>
	/// </summary>
	/// <param name="literals">The literals, each on its line in <paramref name="from"/>.</param>
	/// <param name="from">The text the lines were counted in.</param>
	/// <param name="into">The text to find each literal in.</param>
	/// <param name="theirs">The lines of <paramref name="from"/> the caller's text is on.</param>
	/// <param name="replaced">The parts of <paramref name="into"/> the caller's text took the place of.</param>
	public static IReadOnlyList<int?> LinesIn(
		IReadOnlyList<RewrittenLiteral> literals,
		string from,
		string into,
		(int First, int Count) theirs,
		IReadOnlyList<TextSpan> replaced)
	{
		bool IsTheirs(LiteralValue value)
		{
			var line = LineOf(from, value.Start);

			return line >= theirs.First && line < theirs.First + theirs.Count;
		}

		var all = LiteralValues(from).ToArray();
		var sources = all.Where(value => !IsTheirs(value)).ToArray();
		var targets = LiteralValues(into)
			.Where(value => !replaced.Any(span => span.OverlapsWith(new TextSpan(value.Start, value.Length))))
			.ToArray();

		var taken = new Dictionary<int, int>();
		var found = new List<int?>(literals.Count);

		foreach (var literal in literals)
		{
			// Several literals can open on one line, and each is reported in the order it was read.
			taken.TryGetValue(literal.Line, out var skip);
			taken[literal.Line] = skip + 1;

			var onLine = all.Where(value => LineOf(from, value.Start) == literal.Line).Skip(skip).ToArray();

			if (onLine.Length == 0 || IsTheirs(onLine[0]))
			{
				found.Add(null);
				continue;
			}

			var source = onLine[0];
			var text = from.Substring(source.Start, source.Length);

			var earlier = sources.Count(value => value.Start < source.Start && from.Substring(value.Start, value.Length) == text);
			var matches = targets.Where(value => into.Substring(value.Start, value.Length) == text).ToArray();

			found.Add(matches.Length == 0 ? null : LineOf(into, matches[Math.Min(earlier, matches.Length - 1)].Start));
		}

		return found;
	}

	/// <summary>
	/// A multi-line string literal whose value had line endings rewritten, and how many of them.
	/// </summary>
	/// <param name="Line">
	/// The line it is named by, counted from zero in the text it was found in: the line it opens on, or
	/// that text's first line where the literal opened before it.
	/// </param>
	/// <param name="Endings">How many of the endings inside its value were rewritten.</param>
	public readonly record struct RewrittenLiteral(int Line, int Endings);

	/// <summary>
	/// Says that a multi-line raw literal was moved to sit with the code around it, which is the
	/// other change to a string that nothing else reports.
	/// <para>
	/// What the string says is unchanged, and that is exactly why it needs saying: no analyzer reads a
	/// literal's interior, the diff shows the member rewritten either way, and a caller comparing what
	/// they sent against what landed would otherwise find a difference with no explanation for it.
	/// </para>
	/// </summary>
	/// <param name="lines">How many lines inside the literal moved.</param>
	public static string ReindentedLiteral(int lines) =>
		$"Re-indented {lines} line(s) inside a multi-line raw string literal, so the literal sits with the "
			+ "code around it. What the string says is unchanged, because its value is what is left once the "
			+ "closing delimiter's indentation comes off every line and the delimiter moved with the content "
			+ "-- but the text inside the literal is not the text that was supplied, and no diff separates "
			+ "the two. Its blank lines were left exactly as they arrived.";

	/// <summary>
	/// The parameters <paramref name="text"/> declares, taken as what goes between the parentheses,
	/// laid out one to a line and indented one level in from a declaration sitting at
	/// <paramref name="indent"/> when the caller wrapped them.
	/// <para>
	/// Source text rather than a structured list, because it is what someone writing C# already
	/// knows how to write, and it carries for free everything a structured shape would have to
	/// enumerate: defaults, ref and out, params, attributes, nullable annotations, generic arguments.
	/// It also puts this behind the same promise as everything else here -- if it does not parse,
	/// nothing is written.
	/// </para>
	/// <para>
	/// A wrapped line sits one level in from the declaration rather than level with it, because a
	/// continuation is not a sibling of the signature. The declaration's own indentation alone lands
	/// the list flush under the member it belongs to, and nothing downstream moves it: a continuation
	/// line is not a statement, so Roslyn's formatter has no rule about where it sits, and neither
	/// IDE0055 nor <c>dotnet format</c> has an opinion either.
	/// </para>
	/// <para>
	/// The baseline the caller wrote comes off before that goes on, which is the rule a whole member
	/// goes through. A list written flat, one indented relative to itself, and one already indented
	/// for the destination are one request, and only removing the baseline first makes them so.
	/// </para>
	/// </summary>
	/// <param name="text">The parameters as the caller wrote them.</param>
	/// <param name="options">The destination project's parse options.</param>
	/// <param name="indent">The indentation of the declaration the list belongs to.</param>
	/// <param name="indentUnit">
	/// One level of indentation in the destination file, added to <paramref name="indent"/> to place
	/// every wrapped line. Empty leaves the list exactly as it arrived.
	/// </param>
	public static SeparatedSyntaxList<ParameterSyntax> ParseParameters(
		string text,
		ParseOptions? options,
		string indent = "",
		string indentUnit = "")
	{
		var continuation = indentUnit.Length == 0 ? string.Empty : indent + indentUnit;

		var list = SyntaxFactory.ParseParameterList($"({Arranged(text, continuation)})", options: options);

		var errors = list.GetDiagnostics()
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.ToArray();

		// One column for the parenthesis this added, and no lines at all: a parameter list is not
		// wrapped in anything, so the only offset is the one character.
		if (errors.Length > 0) throw Rejected(text, errors, lineOffset: 0, columnOffset: 1);

		return continuation.Length > 0 && Wrapped(text) ? Opened(list.Parameters, continuation) : list.Parameters;
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
	/// indentation of the line it lands on. Which line that is is asked of the content rather than of
	/// the index, and the blank lines above it are dropped: a fragment that opens with a line break
	/// belongs at the splice point all the same, and keeping the break puts its first line alone at
	/// column zero with the indentation already written sitting on the line above.
	/// </para>
	/// <para>
	/// That is also why the baseline cannot simply be read from the first line. A spliced fragment's
	/// first line is written flush -- there is nothing else to write it against -- so a caller who
	/// indented the rest of it for the destination has a fragment whose baseline is not on the line the
	/// baseline is read from, and every wrapped line then keeps the level it already had and gains
	/// another. <see cref="Written"/> catches that shape: lines already sitting at the destination or
	/// deeper say the fragment was written for the destination, whatever its first line says.
	/// </para>
	/// <para>
	/// The lines of a multi-line literal keep both: leading whitespace there is the value in a
	/// verbatim literal and decides how much is stripped from a raw one, so moving one such line and
	/// not another changes what the program says rather than how it reads. Found here rather than
	/// asked of the caller, since a caller who forgets does not find out.
	/// </para>
	/// </summary>
	/// <param name="code">The fragment as the caller wrote it.</param>
	/// <param name="indent">The indentation of the code it is going beside.</param>
	public static string Reindented(string code, string indent)
	{
		var lines = Split(code);
		var untouched = LiteralLines(code);
		var first = lines.ToList().FindIndex(line => line.Content.Trim().Length > 0);

		var baseline = Written(lines, first, untouched, indent)
			?? Baseline([.. lines.Select(line => line.Content)]);

		// Run even with no baseline to take off and no indentation to put on, since the blank lines above
		// the first line go either way: the line a caller is told a literal is on, counted from the first
		// line with anything on it, then means the same wherever the fragment lands.

		var shifted = lines
			.Select((line, index) => (Line: line, Index: index))
			.Skip(Math.Max(first, 0))
			.Select(entry =>
			{
				if (untouched.Contains(entry.Index)) return entry.Line.Content + entry.Line.Ending;

				var stripped = Stripped(entry.Line.Content, baseline);

				// Padding a blank line only makes trailing whitespace for the next pass to strip again.
				var prefixed = entry.Index > first && stripped.Trim().Length > 0
					? Moved(entry.Line.Content, baseline, indent)
					: stripped;

				return prefixed + entry.Line.Ending;
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
	/// value identical while putting the literal at the indentation of the code around it. A blank
	/// line inside one is left exactly as supplied all the same, since padding it changes the text of
	/// a string and not what the string says: the compiler trims a whitespace-only line to nothing
	/// whatever it holds. What did move is counted, because a caller cannot see it -- the diff shows
	/// the member being rewritten either way, and a literal's interior is content no analyzer reads.
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
	/// <para>
	/// The last copied line is the exception in one respect. What was copied ends partway along it,
	/// since a signature is joined to its body on the line the signature ends on, so the ending that
	/// line comes out with is the first one the caller wrote. It is asked and rewritten with theirs,
	/// and only its content is left alone -- otherwise a verbatim literal opening on that line keeps
	/// its first ending as it arrived while every one after it takes the file's.
	/// </para>
	/// <para>
	/// Only the rewritten endings that lie inside a literal's value are reported. Everywhere else an
	/// ending is layout, and making it the file's is what the whitespace pass does to every line it
	/// writes anyway.
	/// </para>
	/// </summary>
	private static string Shift(
		string code,
		string indent,
		Literal literals,
		string lineEnding,
		Action<IReadOnlyList<RewrittenLiteral>>? rewritten,
		Action<int>? reindented,
		int copied,
		string? given)
	{
		var lines = Split(code);
		var written = lines.Skip(copied).ToArray();
		var baseline = given ?? Baseline([.. written.Select(line => line.Content)]);
		var moved = 0;

		var theirs = Math.Max(copied - 1, 0);
		var wanted = lines.Skip(theirs).All(line => line.Ending is "" or "\n") ? lineEnding : string.Empty;

		var values = LiteralValues(code)
			.Select(value => (Line: LineOf(code, value.Start), Endings: value.Breaks.Select(at => LineOf(code, at)).ToHashSet()))
			.ToArray();

		var inValues = new int[values.Length];

		var shifted = lines.Select((line, index) =>
		{
			var ending = index < theirs ? line.Ending : Ending(line.Ending, wanted);

			if (!string.Equals(ending, line.Ending, StringComparison.Ordinal))
			{
				for (var value = 0; value < values.Length; value++)
				{
					if (values[value].Endings.Contains(index)) inValues[value]++;
				}
			}

			// A copied line is already where it belongs, so both halves of the re-indentation would only
			// move it away from its neighbours.
			if (index < copied) return line.Content + ending;

			if (literals.Verbatim.Contains(index)) return line.Content + ending;

			var raw = literals.Raw.Contains(index);

			// Content of the same kind as the characters around it, and padding it is a change to the
			// text of a string that nothing downstream reports.
			if (raw && line.Content.Trim().Length == 0) return line.Content + ending;

			var stripped = baseline.Length > 0 && line.Content.StartsWith(baseline, StringComparison.Ordinal)
				? line.Content[baseline.Length..]
				: line.Content;

			// The first line's indentation comes from the trivia at the splice point, and padding a
			// blank line only creates trailing whitespace for the next pass to strip again.
			var prefixed = index > 0 && stripped.Trim().Length > 0 ? indent + stripped : stripped;

			if (raw && !string.Equals(prefixed, line.Content, StringComparison.Ordinal)) moved++;

			return prefixed + ending;
		});

		var result = string.Concat(shifted);

		RewrittenLiteral[] changed =
		[
			.. values
				.Select((value, index) => new RewrittenLiteral(value.Line, inValues[index]))
				.Where(literal => literal.Endings > 0),
		];

		if (changed.Length > 0) rewritten?.Invoke(changed);
		if (moved > 0) reindented?.Invoke(moved);

		return result;
	}

	/// <summary>
	/// The ending a line comes out with. A lone LF takes the destination's; anything else is left,
	/// so a caller that wrote a CR LF pair keeps it even in a file that is otherwise LF.
	/// </summary>
	private static string Ending(string arrived, string wanted) =>
		wanted.Length == 0 || arrived != "\n" || wanted == "\n" ? arrived : wanted;

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

	/// <summary>The whitespace a line begins with, which is nothing for a line that begins with content.</summary>
	private static string Leading(string line) =>
		line[..(line.Length - line.TrimStart(' ', '\t').Length)];

	/// <summary>
	/// <paramref name="indent"/> when the fragment's own lines were written for the destination, and
	/// null when the baseline has to be read from the code instead.
	/// <para>
	/// A spliced fragment's first line is written flush -- there is nothing else to write it against
	/// -- so a caller who indented the rest of it for the destination has a fragment whose baseline is
	/// nowhere on the line the baseline is read from, and every wrapped line then keeps the level it
	/// already had and gains another. That is what put six tabs at eleven and three at five.
	/// </para>
	/// <para>
	/// The first line has to carry no indentation of its own: one that does is the fragment's baseline,
	/// written deliberately, and the caller indented everything against it -- an attribute written at
	/// two tabs with its arguments at three wants those arguments one level in from wherever it lands,
	/// not at the destination.
	/// </para>
	/// <para>
	/// Every other line has to read as placed: deeper than the destination and starting with its
	/// indentation, or -- the shape a caller produces by copying a phrase out of the file and editing it
	/// -- at the destination's own depth or one level out, as a continuation and the line that closes it
	/// sit. That second reading needs two levels at least. A line one level deep is what a fragment
	/// written flush produces against a destination one level deep, which is the ordinary case for a
	/// member's attribute, and reading it as absolute would flatten every wrapped line onto the line it
	/// continues. A fragment written flush only reaches two levels by nesting, and nesting passes
	/// through the levels in between, which fails the test on the way.
	/// </para>
	/// <para>
	/// A literal's lines are not layout and are passed over, since their whitespace is the value.
	/// </para>
	/// </summary>
	/// <param name="lines">The fragment's lines, each with its own ending.</param>
	/// <param name="first">The index of its first line with content, which is the spliced one.</param>
	/// <param name="untouched">The lines that sit inside a multi-line literal.</param>
	/// <param name="indent">The indentation of the code the fragment is going beside.</param>
	private static string? Written(
		IReadOnlyList<(string Content, string Ending)> lines,
		int first,
		IReadOnlySet<int> untouched,
		string indent)
	{
		if (indent.Length == 0 || first < 0) return null;
		if (Leading(lines[first].Content).Length > 0) return null;

		var found = false;

		for (var index = first + 1; index < lines.Count; index++)
		{
			if (untouched.Contains(index) || lines[index].Content.Trim().Length == 0) continue;

			var leading = Leading(lines[index].Content);

			var deeper = leading.Length > indent.Length && leading.StartsWith(indent, StringComparison.Ordinal);
			var comparable = leading.StartsWith(indent, StringComparison.Ordinal) || indent.StartsWith(leading, StringComparison.Ordinal);
			var atTheDestination = comparable && Levels(leading) >= 2 && Levels(leading) >= Levels(indent) - 1;

			if (!deeper && !atTheDestination) return null;

			found = true;
		}

		return found ? indent : null;
	}

	/// <summary>How many levels of indentation some leading whitespace is, a tab or four spaces to a level.</summary>
	private static int Levels(string leading) =>
		leading.Count(character => character == '\t') + (leading.Count(character => character == ' ') / 4);

	/// <summary>
	/// A line moved from the fragment's baseline to <paramref name="indent"/>.
	/// <para>
	/// A line shallower than the baseline keeps its distance from it rather than having the destination
	/// put in front of what it had. That is the shape of a replacement copied out of the file with its
	/// first line's indentation: the brace that closes the block above sits two levels out from it, and
	/// adding the destination to the brace's own indentation put it -- and the continuation lines after
	/// it, which no formatting rule moves back -- as far in again as the destination is deep.
	/// </para>
	/// </summary>
	private static string Moved(string line, string baseline, string indent)
	{
		if (line.StartsWith(baseline, StringComparison.Ordinal)) return indent + line[baseline.Length..];

		var leading = Leading(line);

		if (!baseline.StartsWith(leading, StringComparison.Ordinal)) return indent + line;

		var outdent = baseline.Length - leading.Length;

		return indent[..Math.Max(indent.Length - outdent, 0)] + line[leading.Length..];
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
	/// The parameters with the caller's own indentation taken off and the destination's put on: the
	/// first parameter bare, since what precedes it is a parenthesis rather than a line, and every
	/// line after it at <paramref name="continuation"/>.
	/// <para>
	/// The baseline comes off before that goes on, and it is read from the lines after the first: a
	/// list written flat, one indented relative to itself, and one already indented for the
	/// destination are one request, and only removing it makes them so. Reading it from the first
	/// line as well would find nothing to take off in the shape where that line is flush and the rest
	/// are indented under it, and every wrapped line would keep a level it does not want.
	/// </para>
	/// <para>
	/// Blank lines stay blank, since padding one only makes trailing whitespace for the next pass to
	/// strip again.
	/// </para>
	/// </summary>
	private static string Arranged(string text, string continuation)
	{
		if (continuation.Length == 0) return text;

		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		var first = Array.FindIndex(lines, line => line.Trim().Length > 0);
		var last = Array.FindLastIndex(lines, line => line.Trim().Length > 0);

		if (first < 0) return text;
		if (first == last) return lines[first].Trim();

		var baseline = Baseline(lines.Skip(first + 1).ToArray());

		var placed = lines[(first + 1)..(last + 1)].Select(line => line.Trim().Length == 0
			? string.Empty
			: continuation + Stripped(line, baseline));

		return lines[first].Trim() + "\n" + string.Join("\n", placed);
	}

	/// <summary>True when the caller wrote the parameters over more than one line.</summary>
	private static bool Wrapped(string text) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Split('\n')
			.Count(line => line.Trim().Length > 0) > 1;

	/// <summary>
	/// The list with its first parameter carrying the line break and the indentation the rest of them
	/// have, so a wrapped list wraps all the way.
	/// <para>
	/// Set on the parameter rather than left to the text it was parsed from, because whitespace
	/// between a parenthesis and the first parameter is the parenthesis's trailing trivia -- and the
	/// parenthesis a list is parsed with is thrown away, since the one already in the file is the one
	/// that stays. Nothing carried the first line's layout across that, so it landed wherever the
	/// file's own parenthesis happened to leave it: inline with its own indentation where that
	/// parenthesis had no break, and at column zero where it had one. Neither is reported by
	/// anything, since a continuation line is not a statement and no analyzer has an opinion about
	/// where one sits.
	/// </para>
	/// <para>
	/// A line feed rather than the destination's ending, because the whitespace pass that runs over
	/// what was written normalises it, and asking here for an ending this otherwise has no need to
	/// know is one more thing to get wrong.
	/// </para>
	/// </summary>
	private static SeparatedSyntaxList<ParameterSyntax> Opened(
		SeparatedSyntaxList<ParameterSyntax> parameters,
		string continuation)
	{
		if (parameters.Count == 0) return parameters;

		var first = parameters[0]
			.WithLeadingTrivia(SyntaxFactory.LineFeed, SyntaxFactory.Whitespace(continuation));

		return parameters.Replace(parameters[0], first);
	}

	/// <summary>The line with the indentation it was written at taken off, where it has that.</summary>
	private static string Stripped(string line, string baseline) =>
		baseline.Length > 0 && line.StartsWith(baseline, StringComparison.Ordinal)
			? line[baseline.Length..]
			: line;

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
	/// <para>
	/// A branch of an <c>#if</c> the parse took as inactive is disabled text rather than nodes, and its
	/// literals are found by lexing that text: the build that defines the symbol compiles them, and
	/// moving a verbatim literal's line there changes what that build says.
	/// </para>
	/// </summary>
	private static Literal Literals(IReadOnlyList<MemberDeclarationSyntax> members)
	{
		var verbatim = new HashSet<int>();
		var raw = new HashSet<int>();

		void Add(bool isRaw, int start, int end)
		{
			var into = isRaw ? raw : verbatim;

			// From the line after the opening delimiter through the one carrying the closing one: a raw
			// literal's terminator sets the indentation stripped from the rest, so it moves with them or
			// the value changes.
			for (var line = start + 1; line <= end; line++) into.Add(line - WrapperLines);
		}

		foreach (var member in members)
		{
			foreach (var node in member.DescendantNodesAndSelf())
			{
				if (node is not (LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)) continue;

				var span = node.SyntaxTree.GetLineSpan(node.Span);
				if (span.StartLinePosition.Line == span.EndLinePosition.Line) continue;

				Add(IsRaw(node), span.StartLinePosition.Line, span.EndLinePosition.Line);
			}
		}

		// The whole tree rather than the members, because disabled text after the last member is trivia
		// on the wrapper's closing brace.
		var tree = members[0].SyntaxTree;

		foreach (var (span, isRaw) in DisabledLiterals(tree.GetRoot()))
		{
			var lines = tree.GetLineSpan(span);

			Add(isRaw, lines.StartLinePosition.Line, lines.EndLinePosition.Line);
		}

		return new Literal(verbatim, raw);
	}

	/// <summary>
	/// Whether a multi-line literal is a raw one, read from the delimiter it opens with rather than
	/// from a syntax flag, because the interpolated and plain forms carry that in different places.
	/// </summary>
	private static bool IsRaw(SyntaxNode node) => IsRaw(node.ToString());

	/// <summary>Which lines of the supplied code sit inside a literal, and of which kind.</summary>
	private readonly record struct Literal(IReadOnlySet<int> Verbatim, IReadOnlySet<int> Raw);

	/// <summary>Whether a literal's text opens with a raw delimiter, interpolated or not.</summary>
	private static bool IsRaw(string literal) =>
		literal.TrimStart('$', '@').StartsWith("\"\"\"", StringComparison.Ordinal);

	/// <summary>
	/// Every multi-line string literal in <paramref name="code"/>, with where it starts and where each
	/// line break is that belongs to its value rather than to the layout around it.
	/// <para>
	/// Not every break between a literal's delimiters is one. A multi-line raw literal drops the break
	/// after its opening delimiter and the one in front of its closing delimiter's line, so rewriting
	/// either changes nothing the program says. A break inside an interpolation hole is code, spread
	/// over lines the way any expression can be. What is left is where a rewritten ending is a changed
	/// string, and so the only place the rewrite is worth a sentence.
	/// </para>
	/// <para>
	/// Lexed rather than parsed, so it reads a body or a replacement as readily as a member. The lexer
	/// hands an interpolated string over as one token, so that token is parsed on its own to tell its
	/// text from its holes, and a literal nested in a hole is found by the same walk. Every branch of
	/// an <c>#if</c> is read, whichever symbols are defined: see <see cref="Lexed"/>.
	/// </para>
	/// </summary>
	private static IEnumerable<LiteralValue> LiteralValues(string code)
	{
		foreach (var (token, start) in Lexed(code))
		{
			if (token.IsKind(SyntaxKind.InterpolatedStringToken))
			{
				foreach (var node in SyntaxFactory.ParseExpression(token.Text).DescendantNodesAndSelf())
				{
					var nested = node switch
					{
						InterpolatedStringExpressionSyntax interpolated => ValueOf(interpolated, start),
						LiteralExpressionSyntax literal => ValueOf(literal.Token.Text, start + literal.Token.SpanStart),
						_ => null,
					};

					if (nested is { } found) yield return found;
				}

				continue;
			}

			var isString = token.Kind() is SyntaxKind.StringLiteralToken
				or SyntaxKind.Utf8StringLiteralToken
				or SyntaxKind.MultiLineRawStringLiteralToken
				or SyntaxKind.Utf8MultiLineRawStringLiteralToken;

			if (isString && ValueOf(token.Text, start) is { } value) yield return value;
		}
	}

	/// <summary>
	/// The tokens of <paramref name="code"/>, each with where it starts, and the tokens of every branch
	/// of an <c>#if</c> the lexer took as inactive, lexed from the disabled text and placed where that
	/// text sits.
	/// <para>
	/// The lexer defines no symbols, so a branch under <c>#if DEBUG</c> arrives as one run of disabled
	/// text rather than as tokens -- and the build where the branch is active compiles exactly the
	/// literal that text holds. Reading the active branches alone leaves a literal in any other one to
	/// have its endings rewritten and its lines moved with nobody told, which is a change to what the
	/// program says in the one build nobody looked at. A run of disabled text holds no directive, since
	/// the lexer splits it at each one, so lexing it again yields its tokens and nothing else.
	/// </para>
	/// </summary>
	private static IEnumerable<(SyntaxToken Token, int Start)> Lexed(string code)
	{
		foreach (var token in SyntaxFactory.ParseTokens(code))
		{
			foreach (var hidden in Disabled(token.LeadingTrivia)) yield return hidden;

			yield return (token, token.SpanStart);

			foreach (var hidden in Disabled(token.TrailingTrivia)) yield return hidden;
		}

		static IEnumerable<(SyntaxToken Token, int Start)> Disabled(SyntaxTriviaList trivia)
		{
			foreach (var piece in trivia)
			{
				if (!piece.IsKind(SyntaxKind.DisabledTextTrivia)) continue;

				foreach (var (token, start) in Lexed(piece.ToFullString())) yield return (token, piece.SpanStart + start);
			}
		}
	}

	/// <summary>
	/// The string literals in every branch of an <c>#if</c> the parse of <paramref name="root"/> took as
	/// inactive, each by its span in that tree and whether it is a raw one.
	/// <para>
	/// A parse defines the project's symbols, so the branches it leaves inactive are disabled text rather
	/// than nodes, and anything that finds literals by walking the nodes passes over them -- while the
	/// build that defines the symbol compiles exactly the literal that text holds. Whatever protects a
	/// literal's value has to protect these as well, or a line ending or a trailing space inside one is
	/// rewritten as layout in the one build nobody looked at.
	/// </para>
	/// </summary>
	internal static IEnumerable<(TextSpan Span, bool Raw)> DisabledLiterals(SyntaxNode root)
	{
		foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
		{
			if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia)) continue;

			foreach (var (token, at) in Lexed(trivia.ToFullString()))
			{
				var isString = token.Kind() is SyntaxKind.StringLiteralToken
					or SyntaxKind.Utf8StringLiteralToken
					or SyntaxKind.SingleLineRawStringLiteralToken
					or SyntaxKind.MultiLineRawStringLiteralToken
					or SyntaxKind.Utf8MultiLineRawStringLiteralToken
					or SyntaxKind.InterpolatedStringToken;

				if (isString) yield return (new TextSpan(trivia.SpanStart + at, token.Span.Length), IsRaw(token.Text));
			}
		}
	}

	/// <summary>
	/// A plain literal's value breaks: every break in it, less a raw literal's two delimiter breaks.
	/// Null where there are none, which is every literal on one line.
	/// </summary>
	/// <param name="literal">The literal's text.</param>
	/// <param name="start">Where it starts in the code being read.</param>
	private static LiteralValue? ValueOf(string literal, int start)
	{
		var breaks = Breaks(literal, IsRaw(literal));

		return breaks.Count == 0 ? null : new LiteralValue(start, literal.Length, [.. breaks.Select(at => start + at)]);
	}

	/// <summary>
	/// An interpolated literal's value breaks: the ones in its text, and none of the ones inside a hole.
	/// </summary>
	/// <param name="interpolated">The literal, parsed from its token on its own.</param>
	/// <param name="offset">Where that token starts in the code being read.</param>
	private static LiteralValue? ValueOf(InterpolatedStringExpressionSyntax interpolated, int offset)
	{
		var text = interpolated.ToString();
		var parts = interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(part => part.Span).ToArray();

		var breaks = Breaks(text, IsRaw(text))
			.Select(at => interpolated.SpanStart + at)
			.Where(at => parts.Any(part => part.Contains(at)))
			.Select(at => offset + at)
			.ToArray();

		return breaks.Length == 0 ? null : new LiteralValue(offset + interpolated.SpanStart, interpolated.Span.Length, breaks);
	}

	/// <summary>
	/// Where each line break in <paramref name="text"/> starts, with CR, LF and CR LF each one break,
	/// and without the first and last where <paramref name="raw"/> says they are a raw literal's
	/// delimiter lines.
	/// </summary>
	private static IReadOnlyList<int> Breaks(string text, bool raw)
	{
		var breaks = new List<int>();

		for (var index = 0; index < text.Length; index++)
		{
			if (text[index] is not ('\n' or '\r')) continue;

			breaks.Add(index);

			if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
		}

		if (!raw) return breaks;

		return breaks.Count <= 2 ? [] : breaks.GetRange(1, breaks.Count - 2);
	}

	/// <summary>
	/// A multi-line literal, by where it starts, how long it is, and where the line breaks that are part
	/// of its value are, the offsets all into the code it was read from.
	/// </summary>
	private readonly record struct LiteralValue(int Start, int Length, IReadOnlyList<int> Breaks);

	/// <summary>
	/// Names the literals whose endings were rewritten, and the lines they are on.
	/// </summary>
	/// <param name="literals">The literals, each on its line counted from zero.</param>
	/// <param name="ending">The name of the ending they were given.</param>
	/// <param name="where">What the lines are counted in, as it reads after the line numbers.</param>
	private static string Rewrote(IReadOnlyList<RewrittenLiteral> literals, string ending, string where)
	{
		var lines = literals.Select(literal => literal.Line + 1).Distinct().Order().ToArray();
		var one = literals.Count == 1;

		return $"Rewrote {literals.Sum(literal => literal.Endings)} line ending(s) to {ending}, the ending this "
			+ $"file uses, inside the multi-line string literal{(one ? string.Empty : "s")} on {Numbered(lines)} "
			+ $"{where}, which changes {(one ? "its value" : "their values")}.";
	}

	/// <summary>"line 4", "lines 4 and 12", or "lines 2, 4 and 12": every one, since each is a changed value.</summary>
	private static string Numbered(int[] lines) => lines switch
	{
		[] => string.Empty,
		[var only] => $"line {only}",
		[.. var rest, var last] => $"lines {string.Join(", ", rest)} and {last}",
	};

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

		foreach (var (token, at) in Lexed(code))
		{
			var start = LineOf(code, at);
			var end = LineOf(code, at + token.Span.Length - 1);

			// From the line after the opening delimiter through the one carrying the closing one: a raw
			// literal's terminator sets the indentation taken off the rest, so it stays with them.
			for (var line = start + 1; line <= end; line++) lines.Add(line);
		}

		return lines;
	}

	/// <summary>Which line an offset falls on, counted from zero, with CR, LF and CR LF all endings.</summary>
	internal static int LineOf(string code, int offset)
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

	/// <summary>
	/// A sentence naming the lines that cannot be read the way the rest of <paramref name="code"/> is,
	/// or null where the fragment reads consistently.
	/// <para>
	/// A fragment written flush and a fragment written for the destination are both understood, and a
	/// fragment that is half of each is neither: <see cref="Written"/> declines it, so the baseline
	/// falls back to the first line and every line already carrying the destination gains it again.
	/// There is no better reading to switch to -- a fragment spanning a dedent below the splice point
	/// is absolute by construction, and one written flush cannot express "one level out" at all -- so
	/// what is left is to say which lines are on which side rather than to apply the doubling in
	/// silence. A continuation line is not a statement, so nothing downstream reports it: Roslyn's
	/// formatter has no rule that moves one back, and neither IDE0055 nor <c>dotnet format</c> has an
	/// opinion about where a wrapped argument list sits.
	/// </para>
	/// <para>
	/// Every level between column zero and the destination has to be absent for this to be the mixed
	/// shape. A block written flush whose own nesting happens to reach the destination's depth passes
	/// through those levels on the way -- so it is ordinary nesting and is left alone, which is the
	/// difference between the two and the reason this is narrow enough to be worth saying at all.
	/// </para>
	/// </summary>
	/// <param name="code">The fragment as the caller wrote it.</param>
	/// <param name="indent">The indentation of the code it is going beside.</param>
	public static string? MixedIndentation(string code, string indent)
	{
		if (indent.Length == 0) return null;

		var lines = Split(code);
		var untouched = LiteralLines(code);
		var first = lines.ToList().FindIndex(line => line.Content.Trim().Length > 0);

		if (first < 0) return null;

		// A first line carrying indentation is the fragment's own baseline, deliberately written, and
		// every other line is measured against it -- so there is nothing here to be inconsistent with.
		if (Leading(lines[first].Content).Length > 0) return null;
		if (Written(lines, first, untouched, indent) is not null) return null;

		var flush = new List<int>();
		var placed = new List<int>();

		for (var index = first + 1; index < lines.Count; index++)
		{
			if (untouched.Contains(index) || lines[index].Content.Trim().Length == 0) continue;

			var leading = Leading(lines[index].Content);

			if (leading.Length == 0)
			{
				flush.Add(index);
			}
			else if (leading.StartsWith(indent, StringComparison.Ordinal))
			{
				placed.Add(index);
			}
			else
			{
				// A level the destination does not reach: the fragment is nested rather than mixed.
				return null;
			}
		}

		if (flush.Count == 0 || placed.Count == 0) return null;

		return $"The replacement mixes {Lines(flush)} written flush ({Snippet(lines, flush[0])}) with "
			+ $"{Lines(placed)} written at the destination's own indentation ({Snippet(lines, placed[0])}). "
			+ "The flush reading was used, so the indented lines land one level deeper than the rest. "
			+ "Write every line at one baseline or the other.";
	}

	/// <summary>
	/// "line 4" or "lines 2, 3", counting from one so the numbers match what the caller wrote rather
	/// than the array they were found in. Past three it stops listing and says how many there are.
	/// </summary>
	private static string Lines(IReadOnlyList<int> indexes) => indexes.Count switch
	{
		1 => $"line {indexes[0] + 1}",
		<= 3 => $"lines {string.Join(", ", indexes.Select(index => index + 1))}",
		_ => $"{indexes.Count} lines from line {indexes[0] + 1}",
	};

	/// <summary>
	/// One line's content, trimmed and shortened, so the sentence can point at a line without carrying
	/// a wrapped argument list into an error message.
	/// </summary>
	private static string Snippet(IReadOnlyList<(string Content, string Ending)> lines, int index)
	{
		var content = lines[index].Content.Trim();

		return content.Length <= 40 ? $"'{content}'" : $"'{content[..37]}...'";
	}
}
