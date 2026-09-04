using System.Text;

using Microsoft.CodeAnalysis;
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
		var source = text.ToString();
		var builder = new StringBuilder(source.Length + rules.LineEnding.Length);

		foreach (var line in text.Lines)
		{
			// Overlapping rather than touching, so a region beginning where the previous line ends
			// does not claim that line as well and widen the diff by one line for nothing.
			var leaveAlone = protectedSpans.Any(span => span.IntersectsWith(line.SpanIncludingLineBreak))
				|| (within is not null && !within.Any(region => region.OverlapsWith(line.SpanIncludingLineBreak)));

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
