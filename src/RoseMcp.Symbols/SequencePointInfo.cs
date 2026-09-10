namespace RoseMcp.Symbols;

/// <summary>
/// One sequence point out of a method's debug information: an IL offset and the source it came from,
/// or a marker saying that this stretch of IL deliberately maps to nothing.
/// </summary>
public sealed record SequencePointInfo
{
	/// <summary>The IL offset this point starts at.</summary>
	public required int Offset { get; init; }

	/// <summary>
	/// Whether this is a hidden point, which the compiler emits to say that the IL from here on maps
	/// to no source at all.
	/// <para>
	/// It matters because a hidden point must not be reported as a position. Compiler-generated
	/// prologue, an <c>await</c>'s state-machine bookkeeping and the closing brace machinery of an
	/// iterator are all hidden, and answering with the last real point before one would place a
	/// frame on a line whose code is not what is executing.
	/// </para>
	/// </summary>
	public required bool IsHidden { get; init; }

	/// <summary>Where in source, or null when <see cref="IsHidden"/>.</summary>
	public SourcePosition? Position { get; init; }
}
