namespace RoseMcp.Patterns;

/// <summary>
/// A rule that cannot be used as written, with a message that says what to change.
/// <para>
/// An <see cref="ArgumentException"/>, because the rule is an argument the caller wrote and the
/// hosts already turn one into a refusal that names it. The message teaches rather than reports:
/// whoever wrote the rule is usually meeting the grammar for the first time, so a refusal repeats the
/// part of it they just got wrong.
/// </para>
/// </summary>
public sealed class PatternException : ArgumentException
{
	/// <summary>The placeholder grammar in one sentence, for the messages that need to restate it.</summary>
	public const string Grammar = "Placeholders are $name$ for any expression, $name:Type$ for one convertible to Type, "
		+ "$name:id$ for an identifier such as a lambda's parameter, and $T$ where a type argument goes.";

	/// <summary>A rule refused for the reason <paramref name="message"/> gives.</summary>
	public PatternException(string message)
		: base(message)
	{
	}
}
