namespace RoseMcp.Symbols;

/// <summary>
/// A span of source a method's compiled code came from.
/// <para>
/// A span rather than a point because a sequence point is one: a statement wrapped over three lines
/// has one sequence point covering all of it, and reporting only its start would say a call happened
/// on the line the expression began rather than where it is.
/// </para>
/// </summary>
public sealed record SourcePosition
{
	/// <summary>The file as the compiler recorded it, which is an absolute path on the build machine.</summary>
	public required string File { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	public required int EndLine { get; init; }

	public required int EndColumn { get; init; }
}
