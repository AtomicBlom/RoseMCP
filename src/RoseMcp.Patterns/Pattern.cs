using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Patterns;

/// <summary>
/// A find or a replace, parsed as C#, with what each of its placeholders stands for settled by where
/// it is written.
/// <para>
/// A placeholder's kind comes from its position rather than from its spelling, because the position
/// is what the parser already knows: <c>$T$</c> in a type-argument list is a type because the parser
/// put it there, and <c>$x$</c> before a lambda's arrow is an identifier for the same reason. A
/// constraint only ever narrows what a position allows -- <c>:id</c> insists on an identifier, a type
/// after the colon insists on an expression of that type -- so a constraint that contradicts its
/// position is a mistake to refuse rather than a second opinion to weigh.
/// </para>
/// </summary>
public sealed class Pattern
{
	/// <summary>The language version patterns are parsed at: the newest, since a pattern is a query and not code that ships.</summary>
	internal static readonly CSharpParseOptions Options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

	private Pattern(
		string where,
		LexedPattern lexed,
		SyntaxNode root,
		bool isStatement,
		IReadOnlyDictionary<string, Placeholder> placeholders,
		IReadOnlyDictionary<string, int> uses)
	{
		Where = where;
		Lexed = lexed;
		Root = root;
		IsStatement = isStatement;
		Placeholders = placeholders;
		Uses = uses;
	}

	/// <summary>The pattern as the caller wrote it.</summary>
	public string Text => Lexed.Text;

	/// <summary>Whether it is a statement, which a find says by ending in a semicolon.</summary>
	public bool IsStatement { get; }

	/// <summary>Each placeholder by name.</summary>
	public IReadOnlyDictionary<string, Placeholder> Placeholders { get; }

	/// <summary>Which pattern this is, as a message names it: "Rule 3's find".</summary>
	internal string Where { get; }

	/// <summary>The text rewritten as parseable C#.</summary>
	internal LexedPattern Lexed { get; }

	/// <summary>
	/// The parsed pattern: an <see cref="ExpressionSyntax"/>, or a <see cref="StatementSyntax"/> when
	/// <see cref="IsStatement"/>. Its placeholders are identifiers starting with <see cref="PatternLexer.Prefix"/>.
	/// </summary>
	internal SyntaxNode Root { get; }

	/// <summary>How many times each placeholder is written.</summary>
	internal IReadOnlyDictionary<string, int> Uses { get; }

	/// <summary>
	/// The placeholder <paramref name="token"/> is, or false when it is an ordinary token of the pattern.
	/// </summary>
	internal static bool IsPlaceholder(SyntaxToken token, out string name)
	{
		var isOne = token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText.StartsWith(PatternLexer.Prefix, StringComparison.Ordinal);

		name = isOne ? token.ValueText[PatternLexer.Prefix.Length..] : string.Empty;

		return isOne;
	}

	/// <summary>
	/// Parses <paramref name="text"/> as an expression or a statement, or refuses it with a message
	/// that says what to change.
	/// </summary>
	/// <param name="text">The pattern as the caller wrote it.</param>
	/// <param name="asStatement">Parse it as a statement rather than an expression.</param>
	/// <param name="where">Which pattern this is, as a message names it: "Rule 3's find".</param>
	internal static Pattern Parse(string text, bool asStatement, string where)
	{
		if (string.IsNullOrWhiteSpace(text)) throw new PatternException($"{where} is empty.");

		var lexed = PatternLexer.Lex(text, where);

		SyntaxNode root = asStatement
			? SyntaxFactory.ParseStatement(lexed.Code, options: Options, consumeFullText: true)
			: SyntaxFactory.ParseExpression(lexed.Code, options: Options, consumeFullText: true);

		var error = root.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

		if (error is not null)
		{
			var shape = asStatement ? "statement" : "expression";
			var column = lexed.ColumnOf(error.Location.SourceSpan.Start);

			throw new PatternException(
				$"{where} does not parse as a C# {shape}: {error.Id} at column {column}, {error.GetMessage()} "
				+ $"in `{text}`. {PatternException.Grammar}");
		}

		var placeholders = new Dictionary<string, Placeholder>(StringComparer.Ordinal);
		var uses = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var token in root.DescendantTokens())
		{
			if (!IsPlaceholder(token, out var name)) continue;

			var kind = KindAt(token);
			var constraint = lexed.Placeholders.First(placeholder => placeholder.Name == name).Constraint;

			uses[name] = uses.GetValueOrDefault(name) + 1;
			placeholders.TryAdd(name, Settled(where, name, kind, constraint));
		}

		return new Pattern(where, lexed, root, asStatement, placeholders, uses);
	}

	/// <summary>What a placeholder written at <paramref name="token"/> stands for, from its position alone.</summary>
	private static PlaceholderKind KindAt(SyntaxToken token) => token.Parent switch
	{
		ParameterSyntax parameter when parameter.Identifier == token => PlaceholderKind.Identifier,
		ForEachStatementSyntax loop when loop.Identifier == token => PlaceholderKind.Identifier,
		VariableDeclaratorSyntax declarator when declarator.Identifier == token => PlaceholderKind.Identifier,
		SingleVariableDesignationSyntax => PlaceholderKind.Identifier,
		IdentifierNameSyntax name when SyntaxFacts.IsInTypeOnlyContext(name) => PlaceholderKind.Type,
		_ => PlaceholderKind.Expression,
	};

	/// <summary>
	/// The placeholder with its constraint checked against its position, or a refusal where the two
	/// disagree.
	/// </summary>
	private static Placeholder Settled(string where, string name, PlaceholderKind kind, string? constraint)
	{
		var wantsIdentifier = constraint == "id";

		if (wantsIdentifier && kind != PlaceholderKind.Identifier)
		{
			throw new PatternException(
				$"{where} writes ${name}:id$ where an {Describe(kind)} goes. :id is for an identifier, such as the "
				+ $"parameter before a lambda's =>. {PatternException.Grammar}");
		}

		if (wantsIdentifier || constraint is null) return new Placeholder(name, kind, null);

		if (kind != PlaceholderKind.Expression)
		{
			throw new PatternException(
				$"{where} gives ${name}$ the constraint '{constraint}', but it is written where an {Describe(kind)} "
				+ $"goes and only an expression can be constrained by a type. {PatternException.Grammar}");
		}

		return new Placeholder(name, kind, constraint);
	}

	/// <summary>A kind as a message names it.</summary>
	internal static string Describe(PlaceholderKind kind) => kind switch
	{
		PlaceholderKind.Identifier => "identifier",
		PlaceholderKind.Type => "type",
		_ => "expression",
	};
}
