using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Builds a whole body out of a small change to the one that is there.
/// <para>
/// The unit of change is right and the payload is wrong. Changing one line of a sixty-line body
/// means re-emitting sixty, which is tokens spent and fifty-nine lines retyped for the chance to
/// drift -- so an agent correctly notices that a three-line text anchor is cheaper and reaches for
/// one. That is not a wrong granularity, it is an expensive payload for the right one.
/// </para>
/// <para>
/// The answer is not to address a statement by position, which is the thing name addressing exists
/// to avoid. It is to anchor inside <em>one body already resolved by name</em>, so the ambiguity
/// surface is a single member rather than a file. What comes out is still a whole body, handed to
/// the same parse-and-format path as any other, so nothing unbalanced can reach disk -- which is
/// the property that separates this from a text edit and the only reason it is worth building.
/// </para>
/// </summary>
public static class BodyEdit
{
	/// <summary>How many matches an error lists before it starts counting instead.</summary>
	private const int Listed = 5;

	/// <summary>
	/// The body with <paramref name="find"/> replaced, matched on the token stream so indentation and
	/// line endings cannot cause a miss.
	/// <para>
	/// A comment is trivia rather than a token, so the matching cannot see one at all: an anchor
	/// carrying a comment would match on the code around it, leave the comment in the file where it is,
	/// and splice a replacement underneath it. An anchor carrying one is refused for that reason, which
	/// is the only place the token stream being the unit of matching is a limit rather than the point.
	/// </para>
	/// <para>
	/// <paramref name="includeTrivia"/> is the way past that limit for the text a token stream cannot
	/// reach: the words inside a <c>//</c> comment, and the characters inside a string. Matching is then
	/// exact text rather than tokens -- whitespace counts, because inside a comment or a literal it is
	/// the content being edited -- and the replacement is spliced exactly as written for the same reason.
	/// Line endings are the one thing normalised, because an all-LF payload is what composing a string
	/// for a JSON argument produces rather than anything the caller decided; a payload carrying a CR is
	/// matched and spliced exactly as it arrived. What comes out is still a whole body handed to the
	/// parse-and-format path, so a change that would not compile is still refused before anything is
	/// written.
	/// </para>
	/// </summary>
	/// <param name="body">The body as it stands, from the file.</param>
	/// <param name="find">The code to look for, as C#, or the text where <paramref name="includeTrivia"/> is set.</param>
	/// <param name="replace">What to put in its place. Empty removes the matched code.</param>
	/// <param name="includeTrivia">
	/// Match the body's text rather than its tokens, so a match may lie inside a comment or a string.
	/// </param>
	/// <param name="rewritten">
	/// How many of the replacement's line endings were given the body's, so a caller can say so. It is a
	/// change to what a literal says, and it must not be silent.
	/// </param>
	/// <param name="mixed">
	/// That the replacement's own lines disagree about which baseline they were written at, so the
	/// caller can be told rather than left with a splice whose indentation nothing downstream reports.
	/// </param>
	/// <exception cref="ArgumentException">
	/// Nothing matched, more than one thing did, find carries a comment the token matching cannot see,
	/// or the match straddles code and trivia.
	/// </exception>
	public static string Anchored(
		string body,
		string find,
		string replace,
		bool includeTrivia = false,
		Action<int>? rewritten = null,
		Action<string>? mixed = null)
	{
		if (string.IsNullOrWhiteSpace(find))
		{
			throw new ArgumentException("Nothing to find. Pass the code to look for, or use code to write the whole body.");
		}

		if (includeTrivia) return InText(body, find, replace, rewritten);

		var wanted = Tokens(find);

		if (wanted.Count == 0)
		{
			throw new ArgumentException(
				$"'{find.Trim()}' is only whitespace or a comment, and matching is on the tokens. Include the "
					+ "code you mean to change, or pass includeTrivia to match the text instead.");
		}

		if (Comment(find) is { } comment)
		{
			throw new ArgumentException(
				$"find carries a comment ('{comment}'), and matching is on the tokens -- a comment is trivia, so "
					+ "it matches nothing while the code around it matches, and the replacement then lands under "
					+ "the comment already in the file rather than over it. Anchor on the code alone, or pass "
					+ "includeTrivia to match the text and reach the comment itself.");
		}

		var present = Tokens(body);

		var matches = Matches(present, wanted);

		if (matches.Count == 0)
		{
			throw new ArgumentException(
				$"The body does not contain '{First(find)}'. Matching is on the tokens, so whitespace and line "
					+ "breaks do not matter, but every token has to be there in order. Read the body with "
					+ "rose_symbol_info includeSource=true.");
		}

		if (matches.Count > 1)
		{
			throw new ArgumentException(
				$"'{First(find)}' appears {matches.Count} times in the body{Where(present, wanted, matches)}. "
					+ "Extend it until it picks one out, or write the whole body with code.");
		}

		var at = matches[0];
		var start = present[at].SpanStart;
		var end = present[at + wanted.Count - 1].Span.End;

		return string.Concat(body.AsSpan(0, start), Placed(replace, IndentOf(body, start), mixed), body.AsSpan(end));
	}

	/// <summary>
	/// The body with an exact run of text replaced, for the text a token stream cannot see.
	/// <para>
	/// Exact rather than tolerant of whitespace, which is the opposite of the token path and right for
	/// the same reason: inside a comment or a string, whitespace is the content being changed, so a
	/// match that ignored it could not say which of two spacings the caller meant and a replacement
	/// that reflowed it would change the value.
	/// </para>
	/// <para>
	/// Line endings are the exception, and leaving them out of it made this path unreachable. Every
	/// file in a CRLF repository is CRLF and C# composed for a JSON argument is all bare LFs, so an
	/// anchor spanning two lines matched nothing and the refusal named a difference the caller could
	/// not see -- while the advice that fixes it, a CR LF written into the payload, is the one thing a
	/// caller cannot do when the payload is two lines that both want the file's ending.
	/// </para>
	/// <para>
	/// So the needle is tried as written first and given the body's endings only if that found
	/// nothing, and the order is what makes it free. Exact still wins, so a bare LF still reaches a
	/// literal written with bare LFs inside a file that is otherwise CRLF -- which is the shape
	/// <c>rose_format</c> and <c>rose_add_file</c> both report, and the one a caller is most likely to
	/// be editing when they reach for this path at all. Normalising unconditionally would have closed
	/// that case as it opened the other. The replacement follows the needle: if the caller's LFs were
	/// read as transport artefacts in one half they are in both, and if they were taken literally then
	/// they are taken literally in both.
	/// </para>
	/// </summary>
	private static string InText(string body, string find, string replace, Action<int>? rewritten)
	{
		var needle = find;
		var written = replace;
		var changed = 0;
		var matches = Occurrences(body, needle);

		if (matches.Count == 0)
		{
			var ending = Whitespace.Dominant(body);
			var wanted = Normalised(find, ending, out _);

			if (!string.Equals(wanted, find, StringComparison.Ordinal))
			{
				var retried = Occurrences(body, wanted);

				if (retried.Count > 0)
				{
					needle = wanted;
					matches = retried;
					written = Normalised(replace, ending, out changed);
				}
			}
		}

		if (matches.Count == 0)
		{
			throw new ArgumentException(
				$"The body does not contain '{First(find)}'. includeTrivia matches the text exactly, so spacing "
					+ "has to match too -- only the line endings are tried both as written and as the file's own, "
					+ "and only when every one of them was a bare LF. Read the body with rose_symbol_info "
					+ "includeSource=true.");
		}

		if (matches.Count > 1)
		{
			throw new ArgumentException(
				$"'{First(find)}' appears {matches.Count} times in the body. Extend it until it picks one out, or "
					+ "write the whole body with code.");
		}

		var start = matches[0];

		GuardStraddled(body, start, needle.Length);

		if (changed > 0) rewritten?.Invoke(changed);

		// Spliced with none of the re-indentation the token path applies. The point of this path is the
		// text inside a comment or a literal, where leading whitespace is content: a raw literal's
		// indentation decides how much is stripped from its value, and reflowing a comment is a change
		// nobody asked for.
		return string.Concat(body.AsSpan(0, start), written, body.AsSpan(start + needle.Length));
	}

	/// <summary>Where <paramref name="needle"/> appears in <paramref name="body"/>, exactly.</summary>
	private static List<int> Occurrences(string body, string needle)
	{
		var found = new List<int>();

		for (var at = body.IndexOf(needle, StringComparison.Ordinal); at >= 0;
			at = body.IndexOf(needle, at + 1, StringComparison.Ordinal))
		{
			found.Add(at);
		}

		return found;
	}

	/// <summary>
	/// The payload with its line endings rewritten to <paramref name="ending"/>, or exactly as it
	/// arrived where it says anything at all about endings.
	/// <para>
	/// A payload carrying a CR is one whose author is thinking about endings, and an all-LF payload is
	/// what composing a string for a JSON argument produces without anyone deciding to. That is the
	/// same test the written-member path applies, and having one rule rather than two is most of the
	/// point: a caller learns it once, and the escape hatch is the same escape hatch.
	/// </para>
	/// </summary>
	/// <param name="text">The payload as the caller wrote it.</param>
	/// <param name="ending">The ending the body uses.</param>
	/// <param name="changed">How many endings were rewritten, so a caller can be told.</param>
	private static string Normalised(string text, string ending, out int changed)
	{
		changed = 0;

		if (ending == "\n" || text.Contains('\r', StringComparison.Ordinal)) return text;

		changed = text.Count(character => character == '\n');

		return changed == 0 ? text : text.Replace("\n", ending, StringComparison.Ordinal);
	}

	/// <summary>
	/// Refuses a match that covers part of a comment or a string and part of the code around it.
	/// <para>
	/// Such a match is always a mistake and never a cheap one: replacing it rewrites a delimiter, so
	/// what comes out is either unparseable -- caught, but after the caller has been told the anchor
	/// was found -- or parses as something else entirely, with the rest of the body swallowed into a
	/// string. Contained in one comment or one literal, or clear of every one of them, are the two
	/// shapes that mean what the caller thinks they mean.
	/// </para>
	/// </summary>
	private static void GuardStraddled(string body, int start, int length)
	{
		var end = start + length;

		foreach (var (from, to, what) in Protected(body))
		{
			var overlaps = start < to && from < end;
			if (!overlaps) continue;

			var contained = start >= from && end <= to;
			if (contained) return;

			throw new ArgumentException(
				$"The match covers part of {what} and part of the code around it, so replacing it would rewrite a "
					+ "delimiter rather than the text inside one. Anchor entirely inside it, or entirely outside it.");
		}
	}

	/// <summary>
	/// The spans of the body whose content is text rather than code: every comment, and every string
	/// or character literal including the pieces of an interpolated one.
	/// </summary>
	private static IEnumerable<(int From, int To, string What)> Protected(string body)
	{
		foreach (var token in SyntaxFactory.ParseTokens(body))
		{
			foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
			{
				if (MemberSyntax.IsComment(trivia)) yield return (trivia.SpanStart, trivia.Span.End, "a comment");
			}

			var literal = token.Kind() is SyntaxKind.StringLiteralToken
				or SyntaxKind.Utf8StringLiteralToken
				or SyntaxKind.SingleLineRawStringLiteralToken
				or SyntaxKind.MultiLineRawStringLiteralToken
				or SyntaxKind.CharacterLiteralToken
				or SyntaxKind.InterpolatedStringTextToken;

			if (literal) yield return (token.SpanStart, token.Span.End, "a string");
		}
	}

	/// <summary>
	/// The body with code inserted at one end of it. No matching at all, which covers a large share
	/// of real changes: a guard at the top, a log line at the end.
	/// </summary>
	/// <param name="declaration">The member, so an expression body can be refused by name.</param>
	/// <param name="block">The block being added to.</param>
	/// <param name="code">The statements to insert.</param>
	/// <param name="atStart">True for the top of the block, false for the end.</param>
	/// <param name="notices">Where an insertion point worth explaining is recorded.</param>
	public static string Inserted(
		MemberDeclarationSyntax declaration,
		BlockSyntax block,
		string code,
		bool atStart,
		List<string> notices)
	{
		_ = declaration;

		var statements = block.Statements;
		var written = code.Trim();

		if (written.Length == 0) throw new ArgumentException("No code was supplied, so there is nothing to insert.");

		if (statements.Count == 0) return written;

		var existing = string.Join("\n", statements.Select(statement => statement.ToFullString().Trim()));

		if (atStart) return $"{written}\n\n{existing}";

		// Appending after a return, throw, break, continue or goto is unreachable code, which is
		// CS0162 -- a build error in a repository that turns warnings up, and dead code in one that
		// does not. "At the end" means before the jump, which is what a caller adding a log line
		// wants and what they would have written by hand.
		var last = statements[^1];

		if (!IsJump(last)) return $"{existing}\n\n{written}";

		notices.Add($"Inserted before the closing {Keyword(last)}, since anything after it is unreachable.");

		var above = statements.Take(statements.Count - 1)
			.Select(statement => statement.ToFullString().Trim())
			.ToArray();

		var head = above.Length == 0 ? string.Empty : string.Join("\n", above) + "\n\n";

		return $"{head}{written}\n\n{last.ToFullString().Trim()}";
	}

	/// <summary>
	/// Refuses an expression body for an insertion, saying what to do instead. There is no statement
	/// list to insert into, and turning one into a block is a bigger change than was asked for.
	/// </summary>
	public static ArgumentException NoStatements(string signature) =>
		new($"{signature} has an expression body, so there is no statement list to insert into. Pass code with "
			+ "the whole body -- a block in braces is accepted for a member currently written with =>.");

	/// <summary>
	/// The tokens of a piece of code, trivia dropped. Matching on these rather than on text is what
	/// makes an anchor survive a difference of indentation or line ending, which is a real way a text
	/// edit misses in a repository whose files disagree about either.
	/// </summary>
	private static IReadOnlyList<SyntaxToken> Tokens(string code) =>
		[
			.. SyntaxFactory.ParseTokens(code)
				.Where(token => !token.IsKind(SyntaxKind.EndOfFileToken) && token.Span.Length > 0),
		];

	/// <summary>
	/// The first comment in the code, or null where it carries none. Trivia rather than a token, so
	/// nothing the matching does can see it.
	/// </summary>
	private static string? Comment(string code) =>
		SyntaxFactory.ParseTokens(code)
			.SelectMany(token => token.LeadingTrivia.Concat(token.TrailingTrivia))
			.Where(MemberSyntax.IsComment)
			.Select(trivia => First(trivia.ToString()))
			.FirstOrDefault();

	/// <summary>
	/// The replacement laid out for where it lands: the baseline the caller wrote it at taken off
	/// every line and the indentation of the code it replaces put on.
	/// <para>
	/// Without it the two indentations add up, and every line the caller wrapped by hand comes out as
	/// far in again as their own baseline put it. Silently, which is what makes it worth code: a
	/// continuation line is not a statement, so Roslyn's formatter has no rule that moves one back,
	/// and neither IDE0055 nor <c>dotnet format</c> has an opinion about where a wrapped argument list
	/// sits.
	/// </para>
	/// <para>
	/// A payload written half at one baseline and half at the other has no reading that is right for
	/// all of it, and that is reported rather than refused: the doubling it produces is the same
	/// invisible kind, and a caller who cannot see which lines disagree cannot rewrite them.
	/// </para>
	/// </summary>
	private static string Placed(string replace, string indent, Action<string>? mixed)
	{
		if (replace.Length == 0) return replace;

		if (MemberSyntax.MixedIndentation(replace, indent) is { } notice) mixed?.Invoke(notice);

		return MemberSyntax.Reindented(replace, indent);
	}

	/// <summary>
	/// The indentation of the line the match starts on, whether or not the match starts the line.
	/// That is where the replacement is going, so it is the indentation the replacement's own lines
	/// are measured against.
	/// </summary>
	private static string IndentOf(string body, int start)
	{
		var lineStart = start;

		while (lineStart > 0 && body[lineStart - 1] is not ('\n' or '\r')) lineStart--;

		var line = body[lineStart..];

		return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
	}

	/// <summary>Every index in <paramref name="present"/> where <paramref name="wanted"/> starts.</summary>
	private static IReadOnlyList<int> Matches(IReadOnlyList<SyntaxToken> present, IReadOnlyList<SyntaxToken> wanted)
	{
		var found = new List<int>();

		for (var index = 0; index + wanted.Count <= present.Count; index++)
		{
			var same = true;

			for (var offset = 0; offset < wanted.Count; offset++)
			{
				if (string.Equals(present[index + offset].Text, wanted[offset].Text, StringComparison.Ordinal)) continue;

				same = false;
				break;
			}

			if (same) found.Add(index);
		}

		return found;
	}

	private static bool IsJump(StatementSyntax statement) =>
		statement is ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax
			or ContinueStatementSyntax or GotoStatementSyntax;

	private static string Keyword(StatementSyntax statement) => statement switch
	{
		ReturnStatementSyntax => "return",
		ThrowStatementSyntax => "throw",
		BreakStatementSyntax => "break",
		ContinueStatementSyntax => "continue",
		_ => "goto",
	};

	private static string First(string find)
	{
		var line = find.Trim().Split('\n')[0].Trim();

		return line.Length > 60 ? line[..60] + "..." : line;
	}

	/// <summary>Where the matches are, counted in tokens, so a caller can see why it is ambiguous.</summary>
	private static string Where(
		IReadOnlyList<SyntaxToken> present,
		IReadOnlyList<SyntaxToken> wanted,
		IReadOnlyList<int> matches)
	{
		_ = wanted;

		var offsets = matches.Take(Listed).Select(at => present[at].SpanStart).ToArray();

		return $" (at offsets {string.Join(", ", offsets)}{(matches.Count > Listed ? ", ..." : string.Empty)})";
	}
}
