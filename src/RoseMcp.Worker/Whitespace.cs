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
	/// value, so normalising it changes what the program does; and a raw literal's indentation decides
	/// how much is stripped from every line of it, so trimming there rewrites the string's content.
	/// Nothing inside such a literal is touched, and no line that overlaps one is trimmed.
	/// </para>
	/// <para>
	/// <paramref name="within"/> narrows it to the lines an edit actually wrote. A whole-file pass is
	/// right when the caller asked for the file to be formatted and wrong when they asked for one
	/// member to be replaced: a repository whose line endings are already inconsistent would then get
	/// every line of the file rewritten by a one-member change, which buries the edit in a diff
	/// nobody can review. Rewriting only what was written keeps the promise that whatever writes C#
	/// ends formatted, without extending it to text this call never touched.
	/// </para>
	/// </summary>
	public static SourceText Apply(SyntaxNode root, SourceText text, WhitespaceRules rules, TextSpan? within = null)
	{
		var protectedSpans = MultiLineLiterals(root, text);
		var source = text.ToString();
		var builder = new StringBuilder(source.Length + rules.LineEnding.Length);

		foreach (var line in text.Lines)
		{
			// Overlapping rather than touching, so a region beginning where the previous line ends
			// does not claim that line as well and widen the diff by one line for nothing.
			var leaveAlone = protectedSpans.Any(span => span.IntersectsWith(line.SpanIncludingLineBreak))
				|| (within is { } region && !region.OverlapsWith(line.SpanIncludingLineBreak));

			var content = source[line.Span.Start..line.Span.End];

			builder.Append(rules.TrimTrailingWhitespace && !leaveAlone ? content.TrimEnd(' ', '\t') : content);

			// The last line has no break of its own; the final-newline rule below decides whether it
			// gains one.
			if (line.End == line.EndIncludingLineBreak) continue;

			builder.Append(leaveAlone
				? source[line.Span.End..line.EndIncludingLineBreak]
				: rules.LineEnding);
		}

		// The final newline belongs to the end of the file rather than to any line, so a narrowed
		// pass only owns it when the edit reached that far.
		var ownsTheEnd = within is null || within.Value.End >= text.Length;

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
	/// Spans of literals that cross a line, which are the only ones whose whitespace is content. A
	/// single-line literal cannot hold a line ending, and nothing can follow it on its line except
	/// code, so protecting those would only stop ordinary lines from being trimmed.
	/// </summary>
	private static IReadOnlyList<TextSpan> MultiLineLiterals(SyntaxNode root, SourceText text) =>
		[.. root.DescendantNodes()
			.Where(node => node is LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)
			.Select(node => node.Span)
			.Where(span => text.Lines.GetLineFromPosition(span.Start).LineNumber
				!= text.Lines.GetLineFromPosition(span.End).LineNumber)
			.OrderBy(span => span.Start)];

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
