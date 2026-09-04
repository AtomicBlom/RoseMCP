namespace RoseMcp.Contracts;

/// <summary>
/// Where a declaration begins and ends, documentation comment and attributes included.
/// <para>
/// The end is the part worth having. Finding where a member stops was the single most repeated
/// shell command in the session behind these tools -- a grep for the signature, then a read of forty
/// lines after it, purely to work out what a text splice had to replace. A regex approximates that
/// boundary; the compiler knows it exactly, and getting it wrong is how an access modifier gets
/// dropped and a brace duplicated.
/// </para>
/// </summary>
public sealed record DeclarationSpan
{
	public required string FilePath { get; init; }

	/// <summary>One-based first line, counting the documentation comment written above it.</summary>
	public required int StartLine { get; init; }

	/// <summary>One-based last line, which is the line the declaration's own closing brace is on.</summary>
	public required int EndLine { get; init; }

	/// <summary>How many lines it occupies, so a caller can decide whether reading it is worth it.</summary>
	public int LineCount => EndLine - StartLine + 1;
}
