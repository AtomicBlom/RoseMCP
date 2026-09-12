namespace RoseMcp.Contracts;

/// <summary>
/// A page of one stopped thread's call stack.
/// <para>
/// Paged because a deep stack is thousands of frames and a reader wants the innermost few, and
/// because reading a frame costs metadata lookups. <see cref="Total"/> is what was walked, capped,
/// so a caller can tell a stack that ended from one that was cut short.
/// </para>
/// </summary>
public sealed record LiveStackFrames : LiveExecutionReport
{
	/// <summary>The thread walked, which is the held thread unless the caller named another.</summary>
	public int? ThreadId { get; init; }

	/// <summary>The frames in this page, innermost first.</summary>
	public IReadOnlyList<LiveStackFrame> Frames { get; init; } = [];

	/// <summary>Where in the stack this page starts, zero being the innermost frame.</summary>
	public required int Offset { get; init; }

	/// <summary>
	/// How many frames the walk found, which is the number to page through. It is bounded, so a
	/// runaway recursion reports the bound rather than the true depth -- and <see cref="Truncated"/>
	/// says which of the two this is.
	/// </summary>
	public required int Total { get; init; }

	/// <summary>Whether the walk hit its own limit before the stack ran out.</summary>
	public required bool Truncated { get; init; }
}
