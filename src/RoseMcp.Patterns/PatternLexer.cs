using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.Patterns;

/// <summary>
/// Turns a rule's text into C# a parser accepts, by writing each <c>$name$</c> or
/// <c>$name:constraint$</c> as an ordinary identifier.
/// <para>
/// Read with the C# tokenizer rather than a regular expression, because a dollar sign means a
/// placeholder only where it is a token of its own. Inside <c>"$a$"</c>, <c>$"{a}"</c> or a comment
/// it is part of a literal or of trivia, and the tokenizer already knows every one of those forms --
/// raw, verbatim and interpolated strings included -- so none of them has to be recognised here.
/// </para>
/// </summary>
internal static class PatternLexer
{
	/// <summary>
	/// What every placeholder's name is prefixed with once it is an identifier. Chosen to be a name
	/// no code would declare, so a placeholder can never be confused with something in scope.
	/// </summary>
	public const string Prefix = "__rose_ph_";

	/// <summary>
	/// The rule's text with each placeholder written as an identifier, and the placeholders in the
	/// order they appear.
	/// </summary>
	/// <param name="text">The pattern as the caller wrote it.</param>
	/// <param name="where">Which pattern this is, as a message would name it: "Rule 3's find".</param>
	public static LexedPattern Lex(string text, string where)
	{
		var tokens = SyntaxFactory.ParseTokens(text).ToList();
		var code = new StringBuilder(text.Length);
		var placeholders = new List<LexedPlaceholder>();
		var shifts = new List<(int Code, int Text)>();
		var copied = 0;

		for (var index = 0; index < tokens.Count; index++)
		{
			if (!IsDollar(tokens[index])) continue;

			var open = tokens[index];

			if (index + 1 >= tokens.Count || !tokens[index + 1].IsKind(SyntaxKind.IdentifierToken))
			{
				throw new PatternException(
					$"{where} has a $ at column {open.SpanStart + 1} that does not start a placeholder: the name after it "
					+ $"has to be an identifier. {PatternException.Grammar}");
			}

			var name = tokens[index + 1].ValueText;
			var close = index + 2;
			string? constraint = null;

			if (close < tokens.Count && tokens[close].IsKind(SyntaxKind.ColonToken))
			{
				var first = close + 1;

				while (close < tokens.Count && !IsDollar(tokens[close])) close++;

				if (close >= tokens.Count) throw Unclosed(where, name, open);

				if (close == first)
				{
					throw new PatternException($"{where} gives ${name}$ a colon but no constraint after it. {PatternException.Grammar}");
				}

				constraint = text[tokens[first].SpanStart..tokens[close - 1].Span.End].Trim();
			}

			if (close >= tokens.Count || !IsDollar(tokens[close])) throw Unclosed(where, name, open);

			code.Append(text, copied, open.SpanStart - copied);
			shifts.Add((code.Length, open.SpanStart));
			code.Append(Prefix).Append(name);

			copied = tokens[close].Span.End;
			shifts.Add((code.Length, copied));
			placeholders.Add(new LexedPlaceholder(name, constraint, open.SpanStart));

			index = close;
		}

		code.Append(text, copied, text.Length - copied);

		return new LexedPattern(text, code.ToString(), placeholders, shifts);
	}

	/// <summary>Whether a token is a dollar sign standing on its own, which is only ever a placeholder's edge.</summary>
	private static bool IsDollar(SyntaxToken token) => token.IsKind(SyntaxKind.BadToken) && token.Text == "$";

	/// <summary>The refusal for a placeholder with no closing dollar sign.</summary>
	private static PatternException Unclosed(string where, string name, SyntaxToken open) =>
		new($"{where} opens ${name} at column {open.SpanStart + 1} and never closes it with a $. {PatternException.Grammar}");
}

/// <summary>
/// One placeholder as the lexer found it, before its position in the parsed code says what kind it is.
/// </summary>
/// <param name="Name">The name between the dollar signs.</param>
/// <param name="Constraint">What follows the colon, or null when there is no colon.</param>
/// <param name="Column">Where it starts in the rule's own text, zero-based.</param>
internal sealed record LexedPlaceholder(string Name, string? Constraint, int Column);

/// <summary>
/// A pattern's text written as parseable C#, with a way back from a position in the C# to the column
/// the caller wrote, so that a parse error is reported where they would look for it.
/// </summary>
/// <param name="Text">The pattern as the caller wrote it.</param>
/// <param name="Code">The same pattern with each placeholder written as an identifier.</param>
/// <param name="Placeholders">The placeholders in the order they appear.</param>
/// <param name="Shifts">
/// Pairs of equivalent positions, one at each edge of each placeholder, in the code and in the text.
/// </param>
internal sealed record LexedPattern(
	string Text,
	string Code,
	IReadOnlyList<LexedPlaceholder> Placeholders,
	IReadOnlyList<(int Code, int Text)> Shifts)
{
	/// <summary>
	/// The one-based column in <see cref="Text"/> that a position in <see cref="Code"/> came from. A
	/// position inside a placeholder's identifier maps to where the placeholder starts.
	/// </summary>
	public int ColumnOf(int position)
	{
		var column = position;

		for (var index = 0; index < Shifts.Count; index += 2)
		{
			var (codeStart, textStart) = Shifts[index];
			var (codeEnd, textEnd) = Shifts[index + 1];

			if (position < codeStart) break;

			column = position < codeEnd ? textStart : textEnd + (position - codeEnd);
		}

		return column + 1;
	}
}
