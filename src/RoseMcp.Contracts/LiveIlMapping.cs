namespace RoseMcp.Contracts;

/// <summary>
/// How well a frame's instruction pointer maps back to an IL offset, which is how far a reported
/// line can be trusted.
/// <para>
/// Reported rather than folded away, because every value but <see cref="Exact"/> means the line
/// beside it is an approximation, and a reader comparing a stack against source has no other way to
/// know. An optimised frame routinely maps approximately, and saying "line 74" when the runtime said
/// "somewhere near line 74" is the shape of confident wrong answer this surface is built to avoid.
/// </para>
/// </summary>
public enum LiveIlMapping
{
	/// <summary>The offset is exactly where execution is. The line beside it is the line.</summary>
	Exact,

	/// <summary>The nearest offset the runtime could give, which is usual in optimised code.</summary>
	Approximate,

	/// <summary>In the method's prologue, before its first statement.</summary>
	Prolog,

	/// <summary>In the method's epilogue, after its last statement.</summary>
	Epilog,

	/// <summary>The runtime has no IL mapping for this frame at all.</summary>
	NoInfo,

	/// <summary>The address is outside anything the runtime can map, which a native frame produces.</summary>
	UnmappedAddress,
}
