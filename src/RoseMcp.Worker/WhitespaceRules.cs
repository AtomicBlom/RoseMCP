namespace RoseMcp.Worker;

/// <summary>
/// What .editorconfig asks of one file's whitespace, resolved for that file's path.
/// <para>
/// Separate from the formatter because Roslyn's formatter only rewrites the trivia it has reason to
/// touch. Measured: formatting a four-space, LF-terminated file in this repository produces tabs and
/// CRLF on every line it reindents, and leaves the untouched lines exactly as they were -- so a file
/// comes out with mixed endings, which is what IDE0055 then fails the build over.
/// </para>
/// </summary>
public sealed record WhitespaceRules
{
	/// <summary>
	/// What every line should end with. Falls back to whatever the file already mostly uses, because
	/// matching the file is what keeps a format out of the diff when .editorconfig does not say.
	/// </summary>
	public required string LineEnding { get; init; }

	public required bool TrimTrailingWhitespace { get; init; }

	public required bool InsertFinalNewline { get; init; }
}
