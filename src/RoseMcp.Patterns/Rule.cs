using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Patterns;

/// <summary>A rule as the caller wrote it: what to find, and what to write in its place.</summary>
/// <param name="Find">A call, or a call written as a statement ending in a semicolon, with placeholders.</param>
/// <param name="Replace">What a match becomes, using the placeholders the find captured.</param>
public sealed record RuleText(string Find, string Replace);

/// <summary>
/// One rule, parsed, with its find and replace checked against each other.
/// </summary>
public sealed class Rule
{
	private Rule(int number, Pattern find, Pattern replace)
	{
		Number = number;
		Find = find;
		Replace = replace;
	}

	/// <summary>Its place in the list, from one, which is how every message and result names it.</summary>
	public int Number { get; }

	/// <summary>What it finds.</summary>
	public Pattern Find { get; }

	/// <summary>What a match becomes.</summary>
	public Pattern Replace { get; }

	/// <summary>The call at the root of the find: the whole find, or the expression of its statement.</summary>
	internal InvocationExpressionSyntax Root => Find.Root switch
	{
		ExpressionStatementSyntax statement => (InvocationExpressionSyntax)statement.Expression,
		var expression => (InvocationExpressionSyntax)expression,
	};

	/// <summary>The name of the method the find calls, which is all a site has to share to be worth binding.</summary>
	internal string RootName => Invoked(Root)!.Identifier.ValueText;

	/// <summary>
	/// The name an invocation calls: the member of a member access, the member of a conditional access,
	/// or the name itself. Null for an invocation of anything else, such as a delegate returned by a call.
	/// </summary>
	internal static SimpleNameSyntax? Invoked(InvocationExpressionSyntax invocation) => invocation.Expression switch
	{
		MemberAccessExpressionSyntax access => access.Name,
		MemberBindingExpressionSyntax binding => binding.Name,
		SimpleNameSyntax name => name,
		_ => null,
	};

	/// <summary>Parses and checks one rule, or refuses it with a message that says what to change.</summary>
	/// <param name="text">The rule as written.</param>
	/// <param name="number">Its place in the list, from one.</param>
	internal static Rule Parse(RuleText text, int number)
	{
		var findText = (text.Find ?? string.Empty).Trim();
		var isStatement = findText.EndsWith(';');
		var find = Pattern.Parse(findText, isStatement, $"Rule {number}'s find");

		var root = find.Root switch
		{
			ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call } => call,
			InvocationExpressionSyntax call when !isStatement => call,
			_ => null,
		};

		if (root is null || Invoked(root) is null)
		{
			throw new PatternException(
				$"Rule {number}'s find has to be a call, such as `Assert.Equal($e$, $a$)`, or a call written as a statement "
				+ $"ending in a semicolon, such as `Assert.All($xs$, $x$ => $body$);`. It is `{findText}`.");
		}

		var repeated = find.Uses.FirstOrDefault(use => use.Value > 1).Key;

		if (repeated is not null)
		{
			throw new PatternException(
				$"Rule {number}'s find writes ${repeated}$ more than once. A placeholder captures one place, and matching "
				+ "two places that hold the same code is not something a find can say, so give each a name of its own.");
		}

		var replace = Pattern.Parse((text.Replace ?? string.Empty).Trim(), isStatement, $"Rule {number}'s replace");

		foreach (var (name, placeholder) in replace.Placeholders)
		{
			Check(number, find, replace, name, placeholder);
		}

		return new Rule(number, find, replace);
	}

	/// <summary>
	/// Refuses a replace placeholder that its find does not capture, or that it uses in a way the capture
	/// cannot fill.
	/// </summary>
	private static void Check(int number, Pattern find, Pattern replace, string name, Placeholder placeholder)
	{
		if (!find.Placeholders.TryGetValue(name, out var captured))
		{
			var captures = find.Placeholders.Count == 0
				? "captures nothing"
				: "captures " + string.Join(", ", find.Placeholders.Keys.Select(key => $"${key}$"));

			throw new PatternException($"Rule {number}'s replace uses ${name}$, which its find does not capture. Its find {captures}.");
		}

		if (placeholder.Constraint is not null)
		{
			throw new PatternException(
				$"Rule {number}'s replace constrains ${name}$. A constraint says what a find may capture, so it belongs in "
				+ "the find; the replace uses the capture as it is.");
		}

		var fits = (captured.Kind, placeholder.Kind) switch
		{
			(PlaceholderKind.Type, PlaceholderKind.Type) => true,
			(PlaceholderKind.Identifier, PlaceholderKind.Identifier or PlaceholderKind.Expression) => true,
			(PlaceholderKind.Expression, PlaceholderKind.Expression) => true,
			_ => false,
		};

		if (!fits)
		{
			throw new PatternException(
				$"Rule {number}'s replace writes ${name}$ where an {Pattern.Describe(placeholder.Kind)} goes, but its find "
				+ $"captures an {Pattern.Describe(captured.Kind)} there.");
		}

		var isExpression = captured.Kind == PlaceholderKind.Expression;

		if (isExpression && replace.Uses[name] > 1)
		{
			throw new PatternException(
				$"Rule {number}'s replace writes ${name}$ {replace.Uses[name]} times. An expression written twice is "
				+ "evaluated twice, which changes what the code does whenever it has a side effect; a type or an "
				+ "identifier may be repeated.");
		}
	}
}
