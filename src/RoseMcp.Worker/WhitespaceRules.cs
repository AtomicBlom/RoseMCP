namespace RoseMcp.Worker;

/// <summary>
/// How one file's whitespace is laid out: how its lines end, how far a level indents, and the two rules
/// about trailing whitespace and the last line that the formatter leaves alone.
/// <para>
/// Worked out once per file per edit, by <see cref="Whitespace.RulesForAsync"/> from the file as it was,
/// and handed to every pass that writes it -- the formatter as much as the text pass. Two passes that each
/// work it out for themselves are two answers, and the disagreement lands on the lines between them: a
/// formatter told nothing indents the member after an edit with its own four spaces in a file indented
/// with tabs.
/// </para>
/// <para>
/// Separate from the formatter because Roslyn's formatter only rewrites the trivia it has reason to
/// touch. Measured: formatting a four-space, LF-terminated file in this repository produces tabs and
/// CRLF on every line it reindents, and leaves the untouched lines exactly as they were -- so a file
/// comes out with mixed endings, which is what IDE0055 then fails the build over.
/// </para>
/// </summary>
public sealed record WhitespaceRules
{
	/// <summary>What every line written should end with.</summary>
	public required string LineEnding { get; init; }

	public required bool TrimTrailingWhitespace { get; init; }

	public required bool InsertFinalNewline { get; init; }

	/// <summary>
	/// One level of indentation, as the file spells it.
	/// <para>
	/// Here because it is read from the same place at the same time, and needed for the one thing the
	/// formatter cannot do: shift a block of code to the indentation of where it is going. The
	/// formatter reindents statements and moves braces, which are rules it has, but a line the author
	/// wrapped by hand is layout it has no rule about, so it keeps whatever arrived.
	/// </para>
	/// </summary>
	public required string IndentUnit { get; init; }

	/// <summary>How many columns one level is, which the formatter needs as a number whether a level is a tab or spaces.</summary>
	public int IndentSize { get; init; } = 4;

	/// <summary>What decided <see cref="LineEnding"/>.</summary>
	public LayoutSource LineEndingFrom { get; init; } = LayoutSource.Default;

	/// <summary>What decided <see cref="IndentUnit"/> and <see cref="IndentSize"/>.</summary>
	public LayoutSource IndentFrom { get; init; } = LayoutSource.Default;
}
