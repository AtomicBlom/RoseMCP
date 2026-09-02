using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// A cheap value-compare condition for a breakpoint or tracepoint: <c>name OP literal</c>, evaluated
/// on each hit against the stopped frame's arguments and locals. Numeric when both sides parse as
/// numbers, boolean for true/false, otherwise string equality. This is the low-cost path that needs
/// no func-eval; full expression conditions wait for eval. A condition whose variable is not present
/// evaluates false, so the breakpoint simply does not fire.
/// </summary>
internal sealed record BreakpointCondition(string Variable, string Operator, string Literal)
{
	private static readonly string[] Operators = ["==", "!=", "<=", ">=", "<", ">"];

	/// <summary>Parses a condition, or throws with a plain message if it is malformed.</summary>
	public static BreakpointCondition? Parse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;

		foreach (var op in Operators)
		{
			var index = text.IndexOf(op, StringComparison.Ordinal);
			if (index <= 0) continue;

			var variable = text[..index].Trim();
			var literal = text[(index + op.Length)..].Trim();
			if (variable.Length == 0 || literal.Length == 0) break;

			return new BreakpointCondition(variable, op, literal);
		}

		throw new ArgumentException($"Could not parse condition '{text}'. Use name OP literal, e.g. iteration >= 5.");
	}

	public bool Evaluate(IReadOnlyList<LiveVariable> variables)
	{
		var variable = variables.FirstOrDefault(entry => string.Equals(entry.Name, Variable, StringComparison.Ordinal));
		if (variable?.Value is not { } value) return false;

		if (TryNumber(value, out var left) && TryNumber(Literal, out var right))
		{
			return CompareNumbers(left, right);
		}

		if (TryBool(value, out var leftBool) && TryBool(Literal, out var rightBool))
		{
			return Operator switch
			{
				"==" => leftBool == rightBool,
				"!=" => leftBool != rightBool,
				_ => false,
			};
		}

		var v = Unquote(value);
		var l = Unquote(Literal);
		return Operator switch
		{
			"==" => string.Equals(v, l, StringComparison.Ordinal),
			"!=" => !string.Equals(v, l, StringComparison.Ordinal),
			_ => false,
		};
	}

	private bool CompareNumbers(double left, double right) => Operator switch
	{
		"==" => left == right,
		"!=" => left != right,
		"<" => left < right,
		"<=" => left <= right,
		">" => left > right,
		">=" => left >= right,
		_ => false,
	};

	private static bool TryNumber(string text, out double value)
		=> double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

	private static bool TryBool(string text, out bool value) => bool.TryParse(text, out value);

	private static string Unquote(string text)
		=> text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
}
