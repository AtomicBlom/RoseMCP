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
	/// The body with <paramref name="find"/> replaced, matched on the token stream so indentation,
	/// line endings and comments cannot cause a miss.
	/// </summary>
	/// <param name="body">The body as it stands, from the file.</param>
	/// <param name="find">The code to look for, as C#.</param>
	/// <param name="replace">What to put in its place. Empty removes the matched code.</param>
	/// <exception cref="ArgumentException">Nothing matched, or more than one thing did.</exception>
	public static string Anchored(string body, string find, string replace)
	{
		if (string.IsNullOrWhiteSpace(find))
		{
			throw new ArgumentException("Nothing to find. Pass the code to look for, or use code to write the whole body.");
		}

		var wanted = Tokens(find);

		if (wanted.Count == 0)
		{
			throw new ArgumentException(
				$"'{find.Trim()}' is only whitespace or a comment, and matching is on the tokens. Include the "
					+ "code you mean to change.");
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

		return string.Concat(body.AsSpan(0, start), Placed(replace, IndentOf(body, start)), body.AsSpan(end));
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
	/// The replacement laid out for where it lands: the baseline the caller wrote it at taken off
	/// every line and the indentation of the code it replaces put on.
	/// <para>
	/// Without it the two indentations add up, and every line the caller wrapped by hand comes out as
	/// far in again as their own baseline put it. Silently, which is what makes it worth code: a
	/// continuation line is not a statement, so Roslyn's formatter has no rule that moves one back,
	/// and neither IDE0055 nor <c>dotnet format</c> has an opinion about where a wrapped argument list
	/// sits.
	/// </para>
	/// </summary>
	private static string Placed(string replace, string indent) =>
		replace.Length == 0 ? replace : MemberSyntax.Reindented(replace, indent, LiteralLines(replace));

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

	/// <summary>
	/// Which lines of the code sit inside a literal spanning more than one of them, counted from zero.
	/// <para>
	/// Leading whitespace there is the value in a verbatim literal and decides how much is stripped
	/// from a raw one, so re-indenting one such line and not another changes what the program says
	/// rather than how it reads. Left exactly as they arrived here; the pass over the whole member
	/// afterwards knows which kind each is and moves a raw literal with the code around it.
	/// </para>
	/// <para>
	/// A token spanning two lines is a literal by construction -- an identifier, a keyword and a
	/// punctuator each fit on one, and a comment is trivia rather than a token.
	/// </para>
	/// </summary>
	private static IReadOnlySet<int> LiteralLines(string code)
	{
		var lines = new HashSet<int>();

		foreach (var token in Tokens(code))
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
