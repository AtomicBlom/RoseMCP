namespace RoseMcp.Contracts;

/// <summary>
/// Where in source a frame's current instruction came from.
/// <para>
/// A span rather than a point, because a sequence point is one: a statement wrapped over three lines
/// has a single point covering all of it, and reporting only the start says the call happened where
/// the expression began rather than where it is.
/// </para>
/// </summary>
public sealed record LiveSourcePosition
{
	/// <summary>
	/// The file as the compiler recorded it, which is an absolute path on the machine that built the
	/// module. It need not exist here, and a reader matching it against a checkout should match on
	/// the tail rather than the whole.
	/// </summary>
	public required string File { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	public required int EndLine { get; init; }

	public required int EndColumn { get; init; }
}
