using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// The whitespace a formatter does not fix: line endings, trailing spaces, and the final newline.
/// <para>
/// These are the three things a caller writing C# by hand gets wrong, and all three are build errors
/// in a repository that escalates IDE0055. They are applied over the text rather than through the
/// syntax tree because that is the only way to reach a line the formatter had no reason to reindent.
/// </para>
/// <para>
/// It is also where a file's layout is decided, once per write and for both passes: what the repository
/// declares -- an .editorconfig, then for the ending its .gitattributes -- and otherwise what the file
/// already does. Deciding it here rather than beside each pass is what stops the formatter and the text
/// pass giving one file two answers.
/// </para>
/// </summary>
public static class Whitespace
{
	public const string Crlf = "\r\n";
	public const string Lf = "\n";
	public const string Cr = "\r";

	/// <summary>
	/// The layout <paramref name="document"/> is written in, read from it as it stands before anything is
	/// written to it.
	/// <para>
	/// What the repository declares comes first: an .editorconfig covering the file, then for the line
	/// ending its .gitattributes, since once git is told how a checkout's lines end it writes every file
	/// that way. Where neither says, the file's own text does -- the ending most of its lines use and the
	/// way its lines are indented -- and for a file with nothing to read, the files nearest it. Roslyn's
	/// defaults only when all of those are silent.
	/// </para>
	/// <para>
	/// Asked of the file as it was rather than as an edit leaves it, because what a caller writes is not
	/// the repository: a forty-line member composed with bare LFs, written into a ten-line CRLF file, would
	/// otherwise decide that the file is LF.
	/// </para>
	/// </summary>
	public static async Task<WhitespaceRules> RulesForAsync(Document document, CancellationToken cancellationToken)
	{
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var text = await document.GetTextAsync(cancellationToken);
		var root = await document.GetSyntaxRootAsync(cancellationToken);

		return await ResolveAsync(document.Project, document.FilePath, tree, Observe(root, text), cancellationToken);
	}

	/// <summary>
	/// The layout a file that does not exist yet takes at <paramref name="path"/> in
	/// <paramref name="project"/>: what the repository declares for the path, then what
	/// <paramref name="from"/> does where the file is made out of another one, then the files nearest it.
	/// <para>
	/// Never the code a caller supplied for it. That is the file's text rather than the repository's, and
	/// composed for a JSON argument it is LF whatever the repository uses -- so a new file written in its
	/// endings is the one file in a CRLF checkout that is not.
	/// </para>
	/// </summary>
	public static async Task<WhitespaceRules> RulesForNewAsync(
		Project project,
		string path,
		Document? from,
		CancellationToken cancellationToken)
	{
		var observed = from is null
			? default
			: Observe(await from.GetSyntaxRootAsync(cancellationToken), await from.GetTextAsync(cancellationToken));

		return await ResolveAsync(project, path, tree: null, observed, cancellationToken);
	}

	/// <summary>
	/// What the formatter needs to lay <paramref name="document"/> out as <paramref name="rules"/> say: the
	/// document's own options, with each of the four a layout decides filled in where Roslyn was told
	/// nothing about it.
	/// <para>
	/// Roslyn's formatter reads .editorconfig and nothing else, so in a file no .editorconfig speaks for it
	/// indents with four spaces and ends lines with the platform's ending. It applies that to the whitespace
	/// in front of the token after whatever it formats as well as to what was written, so a member edited in
	/// a file indented with tabs comes back with the member after it indented with spaces -- a line nothing
	/// asked to change. Only the gaps are filled, because where Roslyn was told, it read the same
	/// .editorconfig the rules did and knows its own reading of it best -- except where an .editorconfig on
	/// disk applies to the file that the project was never given. Then Roslyn's reading is of the wrong files,
	/// and the rules, which read the disk, decide all four.
	/// </para>
	/// </summary>
	public static async Task<OptionSet> FormattingOptionsAsync(
		Document document,
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		OptionSet options = await document.GetOptionsAsync(cancellationToken);

		if (await document.GetSyntaxTreeAsync(cancellationToken) is not { } tree) return options;

		var told = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);
		var language = document.Project.Language;
		var tabs = rules.IndentUnit == "\t";

		var misread = document.FilePath is { Length: > 0 } path
			&& EditorConfigFiles.NotGiven(document.Project, path).Any(EditorConfigFiles.IsEditorConfig);

		bool Untold(string key) => misread || !told.TryGetValue(key, out _);

		if (Untold("indent_style")) options = options.WithChangedOption(FormattingOptions.UseTabs, language, tabs);
		if (Untold("indent_size")) options = options.WithChangedOption(FormattingOptions.IndentationSize, language, rules.IndentSize);
		if (Untold("tab_width")) options = options.WithChangedOption(FormattingOptions.TabSize, language, rules.IndentSize);
		if (Untold("end_of_line")) options = options.WithChangedOption(FormattingOptions.NewLine, language, rules.LineEnding);

		return options;
	}

	/// <summary>
	/// The indentation the line at <paramref name="position"/> starts with, which is what code written
	/// into that place has to line up with.
	/// </summary>
	internal static string IndentAt(SourceText text, int position)
	{
		var line = text.Lines.GetLineFromPosition(position).ToString();

		return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
	}

	/// <summary>
	/// Rewrites the text to obey <paramref name="rules"/>, leaving multi-line string literals exactly
	/// as they are.
	/// <para>
	/// The exception is not a nicety. A newline inside a verbatim or raw string literal is part of the
	/// value, so normalising it changes what the program does -- measured: a raw literal written with
	/// CRLF and the same one written with LF are different strings, which the compiler confirms. A raw
	/// literal is indentation-sensitive too, so trimming there rewrites the value as well. Nothing
	/// inside such a literal is touched, and no line that overlaps one is trimmed.
	/// </para>
	/// <para>
	/// <paramref name="within"/> narrows it to the lines an edit actually wrote. A whole-file pass is
	/// right when the caller asked for the file to be formatted and wrong when they asked for one
	/// member to be replaced: a repository whose line endings are already inconsistent would then get
	/// every line of the file rewritten by a one-member change, which buries the edit in a diff
	/// nobody can review. Rewriting only what was written keeps the promise that whatever writes C#
	/// ends formatted, without extending it to text this call never touched.
	/// </para>
	/// <para>
	/// Several spans rather than one, because a change can be scattered: a signature change rewrites
	/// a declaration and every call site of it, and the lines between two call sites four hundred
	/// apart were not written by anybody here.
	/// </para>
	/// </summary>
	public static SourceText Apply(
		SyntaxNode root,
		SourceText text,
		WhitespaceRules rules,
		IReadOnlyList<TextSpan>? within = null)
	{
		var protectedSpans = MultiLineLiterals(root, text);
		var delimiters = RawDelimiterLines(root, text, protectedSpans);
		var source = text.ToString();
		var builder = new StringBuilder(source.Length + rules.LineEnding.Length);

		foreach (var line in text.Lines)
		{
			var insideALiteral = protectedSpans.Any(span => span.IntersectsWith(line.SpanIncludingLineBreak))
				&& !delimiters.Contains(line.LineNumber);

			// Overlapping rather than touching, so a region beginning where the previous line ends
			// does not claim that line as well and widen the diff by one line for nothing.
			var outsideTheEdit = within is not null
				&& !within.Any(region => region.OverlapsWith(line.SpanIncludingLineBreak));

			var leaveAlone = insideALiteral || outsideTheEdit;

			var written = source[line.Span.Start..line.Span.End];

			builder.Append(rules.TrimTrailingWhitespace && !leaveAlone ? written.TrimEnd(' ', '\t') : written);

			// The last line has no break of its own; the final-newline rule below decides whether it
			// gains one.
			if (line.End == line.EndIncludingLineBreak) continue;

			builder.Append(leaveAlone
				? source[line.Span.End..line.EndIncludingLineBreak]
				: rules.LineEnding);
		}

		// The final newline belongs to the end of the file rather than to any line, so a narrowed
		// pass only owns it when the edit reached that far.
		var ownsTheEnd = within is null || within.Any(region => region.End >= text.Length);

		if (rules.InsertFinalNewline && ownsTheEnd && builder.Length > 0 && !EndsWithBreak(builder))
		{
			builder.Append(rules.LineEnding);
		}

		var result = builder.ToString();

		return string.Equals(result, source, StringComparison.Ordinal) ? text : SourceText.From(result, text.Encoding);
	}

	/// <summary>The line ending most of this file already uses, for when .editorconfig does not say.</summary>
	public static string Dominant(SourceText text) => Dominant(text.ToString());

	/// <summary>
	/// The line ending most of <paramref name="source"/> uses, or null where it has no line break to read
	/// one from. A tie goes to CRLF, then LF.
	/// </summary>
	public static string? EndingOf(string source)
	{
		var crlf = 0;
		var lf = 0;
		var cr = 0;

		for (var i = 0; i < source.Length; i++)
		{
			if (source[i] == '\n')
			{
				lf++;
				continue;
			}

			if (source[i] != '\r') continue;

			if (i + 1 < source.Length && source[i + 1] == '\n')
			{
				crlf++;
				i++;
				continue;
			}

			cr++;
		}

		if (crlf >= lf && crlf >= cr && crlf > 0) return Crlf;
		if (lf >= cr && lf > 0) return Lf;

		return cr > 0 ? Cr : null;
	}

	/// <summary>
	/// The line ending most of <paramref name="source"/> uses, and the platform's where it has none.
	/// Taken as text rather than as a document, so a payload that is not a file yet can be asked the
	/// same question -- which is the question to ask when matching text in it, and never when choosing
	/// the ending to write, which is the file's layout's to decide.
	/// </summary>
	public static string Dominant(string source) => EndingOf(source) ?? Environment.NewLine;

	/// <summary>
	/// The lines that multi-line literals holding a line ending the rules do not ask for begin on.
	/// <para>
	/// Nothing rewrites them, and that is correct: a newline inside a verbatim or raw literal is part
	/// of the string's value -- measured, the same raw literal written with CRLF and with LF are
	/// different strings, which the compiler confirms. But leaving it at that is how a file comes to
	/// fail <c>dotnet format</c> while no build complains and the obvious fix changes what the
	/// program says, so the consequence is reported where it cannot be fixed.
	/// </para>
	/// <para>
	/// Any disagreeing ending counts, not the literal's dominant one. A hand splice leaves a literal
	/// that is mostly the file's endings with two lines that are not, and those two lines are exactly
	/// what <c>dotnet format</c> fails on -- asking which ending the literal mostly uses would call
	/// that one clean.
	/// </para>
	/// <para>
	/// <paramref name="within"/> narrows it to what an edit wrote, for a caller reporting on its own
	/// change rather than on the file it landed in.
	/// </para>
	/// </summary>
	public static IReadOnlyList<int> LiteralsDisagreeingWith(
		SyntaxNode root,
		SourceText text,
		WhitespaceRules rules,
		TextSpan? within = null)
	{
		var lines = new List<int>();

		foreach (var (literal, _) in Literals(root).OrderBy(literal => literal.Span.Start))
		{
			if (within is { } span && !span.IntersectsWith(literal)) continue;

			var written = text.ToString(literal);
			if (!written.Contains('\n', StringComparison.Ordinal)) continue;
			if (!HoldsAnEndingOtherThan(written, rules.LineEnding)) continue;

			lines.Add(text.Lines.GetLineFromPosition(literal.Start).LineNumber + 1);
		}

		return lines;
	}

	/// <summary>
	/// One sentence about the multi-line literals in a file whose endings are not the file's, or null
	/// where it has none.
	/// <para>
	/// Here rather than beside a caller so <c>rose_format</c> and <c>rose_add_file</c> cannot drift
	/// apart on it. They are the two tools a caller reaches for after writing a file full of literals,
	/// and a file that fails <c>dotnet format</c> on an ENDOFLINE inside one has to be told the same
	/// thing whichever of them was called: nothing rewrites those endings, because a newline inside a
	/// literal is part of the string's value, and the build will not complain either.
	/// </para>
	/// <para>
	/// Grouped into one notice rather than one per literal, because the sentence explaining why they
	/// were left alone is the long part and does not need saying five times.
	/// </para>
	/// </summary>
	/// <param name="root">The file's syntax root, as it now stands.</param>
	/// <param name="text">The file's text, so lines are numbered as they will read.</param>
	/// <param name="rules">What ending the file is supposed to use.</param>
	/// <param name="name">The file's name, for a caller holding several results.</param>
	public static string? LiteralEndingNotice(
		SyntaxNode root,
		SourceText text,
		WhitespaceRules rules,
		string name)
	{
		var lines = LiteralsDisagreeingWith(root, text, rules);

		if (lines.Count == 0) return null;

		var where = lines.Count == 1
			? $"the multi-line string at line {lines[0]}"
			: $"the multi-line strings at lines {string.Join(", ", lines)}";

		return $"{name}: {where} hold line endings the file does not use, and were left "
			+ "alone -- a newline inside a literal is part of the string's value, so rewriting it changes "
			+ "what the program says. dotnet format will still ask for them, and no build will complain. "
			+ "Rewrite the literal with the file's own endings if the value allows it.";
	}

	/// <summary>Whether any line break in the text is something other than <paramref name="ending"/>.</summary>
	private static bool HoldsAnEndingOtherThan(string written, string ending)
	{
		for (var index = 0; index < written.Length; index++)
		{
			if (written[index] is not ('\r' or '\n')) continue;

			var length = written[index] == '\r' && index + 1 < written.Length && written[index + 1] == '\n' ? 2 : 1;

			if (!string.Equals(written.Substring(index, length), ending, StringComparison.Ordinal)) return true;

			index += length - 1;
		}

		return false;
	}

	/// <summary>
	/// Spans of literals that cross a line, whose trailing whitespace is the value of a string
	/// rather than layout. A single-line literal cannot hold a line ending, and nothing can follow
	/// it on its line except code, so protecting those would only stop ordinary lines from being
	/// trimmed.
	/// <para>
	/// The literals of an <c>#if</c> branch the parse took as inactive are among them, found in its
	/// disabled text: the build that defines the symbol compiles them, so an ending or a trailing space
	/// rewritten there is a changed value in that build.
	/// </para>
	/// </summary>
	private static IReadOnlyList<TextSpan> MultiLineLiterals(SyntaxNode root, SourceText text) =>
		[.. Crossing(LiteralSpans(root), text)];

	/// <summary>
	/// Every string literal in <paramref name="root"/> by its span and whether it is a raw one: the nodes
	/// of the active code, and the literals lexed out of every inactive branch's disabled text.
	/// </summary>
	private static IEnumerable<(TextSpan Span, bool Raw)> Literals(SyntaxNode root) =>
		root.DescendantNodes()
			.Where(node => node is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)
			.Select(node => (node.Span, IsRaw(node)))
			.Concat(MemberSyntax.DisabledLiterals(root));

	/// <summary>The spans of every string literal in <paramref name="root"/>, inactive branches included.</summary>
	private static IEnumerable<TextSpan> LiteralSpans(SyntaxNode root) => Literals(root).Select(literal => literal.Span);

	/// <summary>
	/// The lines a multi-line raw literal's delimiters sit on.
	/// <para>
	/// Neither is content. A raw literal's value begins after the line break that follows its opening
	/// quotes and stops before the one in front of its closing quotes, so those two breaks are the
	/// literal's punctuation rather than part of what it says -- and leaving them as they arrived is
	/// how a CRLF file keeps a lone LF that dotnet format rejects and no build mentions. The lines
	/// between them are content and stay exactly as they are.
	/// </para>
	/// <para>
	/// Raw literals only. Every break inside a verbatim literal is part of its value, the first one
	/// included, so a verbatim literal has no delimiter line to speak of.
	/// </para>
	/// <para>
	/// A delimiter inside another literal is not one: a raw literal written into an interpolation of
	/// a multi-line one has quotes that are the outer literal's content, and normalising the line
	/// they sit on would rewrite what the outer one says.
	/// </para>
	/// </summary>
	private static IReadOnlySet<int> RawDelimiterLines(
		SyntaxNode root,
		SourceText text,
		IReadOnlyList<TextSpan> literals)
	{
		var lines = new HashSet<int>();

		foreach (var span in Crossing(Literals(root).Where(literal => literal.Raw).Select(literal => literal.Span), text))
		{
			if (literals.Any(other => other != span && other.Contains(span))) continue;

			lines.Add(text.Lines.GetLineFromPosition(span.Start).LineNumber);
			lines.Add(text.Lines.GetLineFromPosition(span.End - 1).LineNumber);
		}

		return lines;
	}

	/// <summary>
	/// True for a literal written with raw quotes, read off the delimiter it opens with rather than
	/// guessed at from its content.
	/// </summary>
	private static bool IsRaw(SyntaxNode node) => node switch
	{
		LiteralExpressionSyntax literal => literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken),
		InterpolatedStringExpressionSyntax interpolated =>
			interpolated.StringStartToken.IsKind(SyntaxKind.InterpolatedMultiLineRawStringStartToken),
		_ => false,
	};

	/// <summary>The spans that start and end on different lines.</summary>
	private static IEnumerable<TextSpan> Crossing(IEnumerable<TextSpan> spans, SourceText text) =>
		spans
			.Where(span => text.Lines.GetLineFromPosition(span.Start).LineNumber
				!= text.Lines.GetLineFromPosition(span.End).LineNumber)
			.OrderBy(span => span.Start);

	private static bool EndsWithBreak(StringBuilder builder) =>
		builder[^1] is '\n' or '\r';

	/// <summary>
	/// How many files beside a new one are read for its layout: enough that one odd file cannot decide for
	/// a directory, and few enough that starting a file does not read a project.
	/// </summary>
	private const int NeighboursRead = 5;

	/// <summary>The .editorconfig keys a layout is made of, which are all a layout reads.</summary>
	private static readonly string[] LayoutKeys =
	[
		"end_of_line",
		"indent_style",
		"indent_size",
		"tab_width",
		"trim_trailing_whitespace",
		"insert_final_newline",
	];

	private static readonly char[] Separators = ['\\', '/'];

	/// <summary>
	/// The layout of the file at <paramref name="path"/>, from what declares one and then from
	/// <paramref name="own"/>, which is what the file itself shows. Only a rooted path is asked of the disk
	/// or of git, since a relative one would be measured from wherever this process happens to be.
	/// </summary>
	private static async Task<WhitespaceRules> ResolveAsync(
		Project project,
		string? path,
		SyntaxTree? tree,
		Observation own,
		CancellationToken cancellationToken)
	{
		var rooted = path is { Length: > 0 } && Path.IsPathRooted(path) ? path : null;
		var declared = Declared(project, rooted, tree);

		// Read only when the file itself has nothing to say, which for a file that exists is almost never.
		var neighbours = new Lazy<Task<Observation>>(() =>
			rooted is null ? Task.FromResult(default(Observation)) : NeighboursAsync(project, rooted, cancellationToken));

		var ending = await EndingAsync(declared, rooted, own, neighbours);
		var indent = await IndentationAsync(declared, own, neighbours);

		return new WhitespaceRules
		{
			LineEnding = ending.Value,
			LineEndingFrom = ending.From,
			IndentUnit = indent.Value.Unit,
			IndentSize = indent.Value.Size,
			IndentFrom = indent.From,
			TrimTrailingWhitespace = Flag(declared, "trim_trailing_whitespace") ?? false,
			InsertFinalNewline = Flag(declared, "insert_final_newline") ?? false,
		};
	}

	private static async Task<(string Value, LayoutSource From)> EndingAsync(
		IReadOnlyDictionary<string, string> declared,
		string? path,
		Observation own,
		Lazy<Task<Observation>> neighbours)
	{
		if (Ending(declared) is { } configured) return (configured, LayoutSource.EditorConfig);
		if (path is not null && GitAttributes.LineEndingFor(path) is { } attributed) return (attributed, LayoutSource.GitAttributes);
		if (own.Ending is { } found) return (found, LayoutSource.File);
		if ((await neighbours.Value).Ending is { } near) return (near, LayoutSource.Neighbours);

		return (Environment.NewLine, LayoutSource.Default);
	}

	private static async Task<(Indentation Value, LayoutSource From)> IndentationAsync(
		IReadOnlyDictionary<string, string> declared,
		Observation own,
		Lazy<Task<Observation>> neighbours)
	{
		if (Indent(declared) is { } configured) return (configured, LayoutSource.EditorConfig);
		if (own.Indent is { } found) return (found, LayoutSource.File);
		if ((await neighbours.Value).Indent is { } near) return (near, LayoutSource.Neighbours);

		return (new Indentation("    ", 4), LayoutSource.Default);
	}

	/// <summary>
	/// What .editorconfig says about the file: Roslyn's reading of the files the project was given, and, where an
	/// .editorconfig on disk applies to this path that the project was not given, the files on disk -- which is the
	/// difference between a project the design-time build has described and one it has not yet.
	/// <para>
	/// The disk wins wherever it speaks. It holds every file Roslyn was given, which the sweep keeps in step with
	/// what was read, and the ones it was not: one in a folder that held no source when the project was built, or
	/// above a project that held none at all. A nearer file the project was never given is exactly the one whose
	/// settings Roslyn's reading lacks.
	/// </para>
	/// </summary>
	private static IReadOnlyDictionary<string, string> Declared(Project project, string? path, SyntaxTree? tree)
	{
		// A tree the project has never seen is answered by its path, which is all a file not yet written has.
		var asked = tree ?? (path is null ? null : CSharpSyntaxTree.ParseText(string.Empty, path: path));
		var given = asked is null ? null : project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(asked);
		var declared = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var key in LayoutKeys)
		{
			if (given is not null && given.TryGetValue(key, out var value)) declared[key] = value;
		}

		if (path is null) return declared;

		var unread = EditorConfigFiles.NotGiven(project, path).Any(EditorConfigFiles.IsEditorConfig);
		if (!unread) return declared;

		var onDisk = EditorConfigFiles.For(path);

		foreach (var key in LayoutKeys)
		{
			if (onDisk.TryGetValue(key, out var value)) declared[key] = value;
		}

		return declared;
	}

	/// <summary>What a file's text says of its own layout.</summary>
	private static Observation Observe(SyntaxNode? root, SourceText text) => new(EndingOf(text.ToString()), IndentOf(root, text));

	/// <summary>
	/// How a file indents, read off its lines: with a tab where more of its indented lines begin with one,
	/// and otherwise with the step its space-indented lines most often go in by. Null where no line is
	/// indented, or as many begin with a tab as with a space.
	/// <para>
	/// A line inside a multi-line literal is left out, since its whitespace is a string's rather than the
	/// file's. A line wrapped to line up under the one above goes in by an odd number of spaces, and is
	/// outvoted by the lines that go in a level at a time.
	/// </para>
	/// </summary>
	private static Indentation? IndentOf(SyntaxNode? root, SourceText text)
	{
		var literals = root is null ? [] : MultiLineLiterals(root, text);
		var steps = new int[9];
		var tabs = 0;
		var spaces = 0;
		var previous = 0;

		foreach (var line in text.Lines)
		{
			var content = text.ToString(line.Span);

			if (string.IsNullOrWhiteSpace(content)) continue;
			if (literals.Any(span => span.Start < line.Start && line.Start < span.End)) continue;

			if (content[0] == '\t')
			{
				tabs++;
				continue;
			}

			var width = content.Length - content.TrimStart(' ').Length;
			var step = width - previous;

			previous = width;

			if (width == 0) continue;

			spaces++;

			if (step is > 1 and < 9) steps[step]++;
		}

		if (tabs == spaces) return null;
		if (tabs > spaces) return new Indentation("\t", 4);

		var size = steps.Max() == 0 ? 4 : Array.IndexOf(steps, steps.Max());

		return new Indentation(new string(' ', size), size);
	}

	/// <summary>
	/// What the files nearest <paramref name="path"/> in its project do, for a file with nothing of its own
	/// to read: the ending and the indentation most of them use, the nearest breaking a tie. Files the build
	/// or a designer writes are left out, since how a generator lays out its output says nothing about the
	/// repository.
	/// </summary>
	private static async Task<Observation> NeighboursAsync(Project project, string path, CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(path) ?? string.Empty;

		var nearest = project.Documents
			.Where(document => document.FilePath is { } file && !SamePath(file, path) && !Generated(file))
			.OrderByDescending(document => Shared(directory, Path.GetDirectoryName(document.FilePath!) ?? string.Empty))
			.ThenBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
			.Take(NeighboursRead)
			.ToArray();

		var endings = new List<string>();
		var indents = new List<Indentation>();

		foreach (var document in nearest)
		{
			var seen = Observe(
				await document.GetSyntaxRootAsync(cancellationToken),
				await document.GetTextAsync(cancellationToken));

			if (seen.Ending is { } ending) endings.Add(ending);
			if (seen.Indent is { } indent) indents.Add(indent);
		}

		return new Observation(Most(endings), Most(indents));
	}

	/// <summary>The value most of <paramref name="values"/> are, the earliest breaking a tie.</summary>
	private static T? Most<T>(IReadOnlyList<T> values)
		where T : class =>
		values.GroupBy(value => value).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key;

	/// <summary>How many directories two paths share from the top, which is how near two files are.</summary>
	private static int Shared(string directory, string other)
	{
		var left = directory.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
		var right = other.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
		var shared = 0;

		while (shared < left.Length && shared < right.Length
			&& string.Equals(left[shared], right[shared], StringComparison.OrdinalIgnoreCase))
		{
			shared++;
		}

		return shared;
	}

	/// <summary>Whether a file is one the build or a designer writes rather than a person.</summary>
	private static bool Generated(string file) => GeneratedCode.IsGeneratedFile(file);

	private static bool SamePath(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

	private static string? Ending(IReadOnlyDictionary<string, string> declared)
	{
		if (!declared.TryGetValue("end_of_line", out var value)) return null;

		return value.Trim().ToLowerInvariant() switch
		{
			"crlf" => Crlf,
			"lf" => Lf,
			"cr" => Cr,
			_ => null,
		};
	}

	private static bool? Flag(IReadOnlyDictionary<string, string> declared, string key)
	{
		if (!declared.TryGetValue(key, out var value)) return null;

		return bool.TryParse(value.Trim(), out var parsed) ? parsed : null;
	}

	/// <summary>
	/// How .editorconfig says to indent, or null where it says nothing. indent_style decides between a tab
	/// and spaces and indent_size how many columns a level is; an indent_size with no indent_style means
	/// spaces, which is how Roslyn's formatter reads it, since spaces are its own default.
	/// </summary>
	private static Indentation? Indent(IReadOnlyDictionary<string, string> declared)
	{
		var style = declared.TryGetValue("indent_style", out var written) ? written.Trim().ToLowerInvariant() : null;
		var size = Columns(declared, "indent_size");

		if (style == "tab") return new Indentation("\t", size ?? Columns(declared, "tab_width") ?? 4);
		if (style == "space" || size is not null) return new Indentation(new string(' ', size ?? 4), size ?? 4);

		return null;
	}

	/// <summary>A width .editorconfig gives, or null where it gives none a formatter could use.</summary>
	private static int? Columns(IReadOnlyDictionary<string, string> declared, string key) =>
		declared.TryGetValue(key, out var value) && int.TryParse(value.Trim(), out var columns) && columns is > 0 and <= 16
			? columns
			: null;

	/// <summary>What a file's text says of its own layout, each part null where the text has nothing to go on.</summary>
	private readonly record struct Observation(string? Ending, Indentation? Indent);

	/// <summary>One level of indentation: what is written, and how many columns it is.</summary>
	private sealed record Indentation(string Unit, int Size);
}
