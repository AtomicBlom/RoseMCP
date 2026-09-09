using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// The whitespace a formatter does not fix: line endings, trailing spaces, and the final newline.
/// <para>
/// These are the three things a caller writing C# by hand gets wrong, and all three are build errors
/// in a repository that escalates IDE0055. They are applied over the text rather than through the
/// syntax tree because that is the only way to reach a line the formatter had no reason to reindent.
/// </para>
/// </summary>
public static class Whitespace
{
	public const string Crlf = "\r\n";
	public const string Lf = "\n";
	public const string Cr = "\r";

	/// <summary>What .editorconfig asks of this file, falling back to what the file already does.</summary>
	public static WhitespaceRules RulesFor(Project project, SyntaxTree tree, SourceText text)
	{
		var options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);

		return new WhitespaceRules
		{
			LineEnding = Ending(options) ?? Dominant(text),
			TrimTrailingWhitespace = Flag(options, "trim_trailing_whitespace") ?? false,
			InsertFinalNewline = Flag(options, "insert_final_newline") ?? false,
			IndentUnit = Indent(options),
		};
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
	public static string Dominant(SourceText text)
	{
		var source = text.ToString();
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

		return cr > 0 ? Cr : Environment.NewLine;
	}

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

		foreach (var node in root.DescendantNodes())
		{
			if (node is not (LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)) continue;
			if (within is { } span && !span.IntersectsWith(node.Span)) continue;

			var written = node.ToString();
			if (!written.Contains('\n', StringComparison.Ordinal)) continue;
			if (!HoldsAnEndingOtherThan(written, rules.LineEnding)) continue;

			lines.Add(text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1);
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
	/// </summary>
	private static IReadOnlyList<TextSpan> MultiLineLiterals(SyntaxNode root, SourceText text) =>
		[.. Crossing(root.DescendantNodes()
			.Where(node => node is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax), text)];

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

		foreach (var span in Crossing(root.DescendantNodes().Where(IsRaw), text))
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

	/// <summary>The spans of those nodes that start and end on different lines.</summary>
	private static IEnumerable<TextSpan> Crossing(IEnumerable<SyntaxNode> nodes, SourceText text) =>
		nodes
			.Select(node => node.Span)
			.Where(span => text.Lines.GetLineFromPosition(span.Start).LineNumber
				!= text.Lines.GetLineFromPosition(span.End).LineNumber)
			.OrderBy(span => span.Start);

	private static bool EndsWithBreak(StringBuilder builder) =>
		builder[^1] is '\n' or '\r';

	private static string? Ending(AnalyzerConfigOptions options)
	{
		if (!options.TryGetValue("end_of_line", out var value)) return null;

		return value.Trim().ToLowerInvariant() switch
		{
			"crlf" => Crlf,
			"lf" => Lf,
			"cr" => Cr,
			_ => null,
		};
	}

	private static bool? Flag(AnalyzerConfigOptions options, string key)
	{
		if (!options.TryGetValue(key, out var value)) return null;

		return bool.TryParse(value.Trim(), out var parsed) ? parsed : null;
	}

	/// <summary>
	/// One level of indentation: a tab, or as many spaces as indent_size asks for. Four spaces when
	/// the file says nothing, which is the language's own default and so the likeliest thing a file
	/// with no .editorconfig already uses.
	/// </summary>
	private static string Indent(AnalyzerConfigOptions options)
	{
		var tabs = options.TryGetValue("indent_style", out var style)
			&& style.Trim().Equals("tab", StringComparison.OrdinalIgnoreCase);

		if (tabs) return "\t";

		var width = options.TryGetValue("indent_size", out var size)
			&& int.TryParse(size.Trim(), out var parsed)
			&& parsed is > 0 and <= 16
				? parsed
				: 4;

		return new string(' ', width);
	}
}
