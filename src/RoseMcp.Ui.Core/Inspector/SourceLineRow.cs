namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One row of a method's source as a reader clicks through it: a line of code, or a place inside
/// that line where execution can be stopped.
/// </summary>
public sealed class SourceLineRow
{
	/// <summary>The line in the file, counting from one.</summary>
	public required int Line { get; init; }

	/// <summary>The line number as shown, padded so the code starts at one column.</summary>
	public required string LineLabel { get; init; }

	/// <summary>The code, verbatim. Empty on a continuation row, which has the line above it.</summary>
	public required string Text { get; init; }

	/// <summary>
	/// The location a breakpoint set from this row is given, or null when nothing stops on this
	/// line. It names the method the instructions belong to, which need not be the one being read.
	/// </summary>
	public string? Location { get; init; }

	/// <summary>
	/// The method those instructions are in, when it is not the one being read, and empty when it
	/// is. A line inside a lambda is the case that matters: a reader who is not told has no way to
	/// see that their click went somewhere other than the method in front of them.
	/// </summary>
	public string Note { get; init; } = string.Empty;

	/// <summary><c>IL_0007</c>, or empty when nothing stops on this line.</summary>
	public string OffsetLabel { get; init; } = string.Empty;

	/// <summary>
	/// Whether this row is a second or later place to stop on a line the row above already shows. It
	/// carries no code of its own, only the method and offset that make it somewhere else.
	/// </summary>
	public bool IsContinuation { get; init; }

	/// <summary>
	/// Whether execution is sitting on this line. Only ever true for a stopped frame's source, and
	/// it is what a step is watched through: the value of a step is seeing the highlight move.
	/// </summary>
	public bool IsCurrent { get; init; }

	/// <summary>Whether a breakpoint can be set from this row at all.</summary>
	public bool IsBreakable => Location is not null;

	/// <summary>Whether there is a method to name beside this row.</summary>
	public bool HasNote => Note.Length > 0;
}
