namespace RoseMcp.Contracts;

/// <summary>
/// One managed frame of a stopped thread's call stack, with everything needed to find the code it
/// is in and to know how far that answer can be trusted.
/// </summary>
public sealed record LiveStackFrame
{
	/// <summary>
	/// Position on the thread's stack, innermost first and zero-based. It is how a caller asks for
	/// this frame's variables, and it counts only frames reported here, so the two cannot disagree.
	/// </summary>
	public required int Index { get; init; }

	public required int ThreadId { get; init; }

	/// <summary>The module the method is in, by file name.</summary>
	public string? Module { get; init; }

	/// <summary>The method, as <c>Namespace.Type.Method</c>. Null when metadata could not name it.</summary>
	public string? MethodFullName { get; init; }

	/// <summary>Where in the method's IL execution is, or null when the runtime would not say.</summary>
	public int? IlOffset { get; init; }

	/// <summary>How well <see cref="IlOffset"/> maps to where execution actually is.</summary>
	public required LiveIlMapping Mapping { get; init; }

	/// <summary>Whether symbols were available for this frame's module, and if not, why.</summary>
	public required LiveSymbolState Symbols { get; init; }

	/// <summary>The source this frame is in, when symbols and a sequence point could give one.</summary>
	public LiveSourcePosition? Source { get; init; }

	/// <summary>
	/// Whether this is the frame the debugger is holding -- the one an evaluation resolves names
	/// against, and the one a reader wants selected when a stack first appears.
	/// </summary>
	public required bool IsActive { get; init; }

	/// <summary>
	/// How many frames immediately below this one could not be represented: native, internal, or
	/// dynamic frames the runtime gives no function for.
	/// <para>
	/// Counted rather than dropped, because a stack silently missing three frames reads as a
	/// complete stack with a surprising caller. A number a reader can see is the difference between
	/// "there is a native transition here" and "this method was called by that one", which are
	/// different facts.
	/// </para>
	/// </summary>
	public required int SkippedBefore { get; init; }
}
