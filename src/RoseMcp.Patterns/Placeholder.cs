namespace RoseMcp.Patterns;

/// <summary>What a placeholder stands for, decided by where it is written.</summary>
public enum PlaceholderKind
{
	/// <summary>An expression, or a lambda's body, which may be a block.</summary>
	Expression,

	/// <summary>An identifier: a lambda's parameter in a find, a loop variable in a replace.</summary>
	Identifier,

	/// <summary>A type, where a type argument or any other type is written.</summary>
	Type,
}

/// <summary>
/// One named hole in a pattern.
/// </summary>
/// <param name="Name">The name between the dollar signs.</param>
/// <param name="Kind">What it stands for.</param>
/// <param name="Constraint">
/// The type an expression placeholder's capture has to convert to, as written after the colon, or
/// null when it takes any expression.
/// </param>
public sealed record Placeholder(string Name, PlaceholderKind Kind, string? Constraint);
