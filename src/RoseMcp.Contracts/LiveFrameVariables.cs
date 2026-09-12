namespace RoseMcp.Contracts;

/// <summary>
/// The arguments and locals of one frame of a stopped thread.
/// <para>
/// Per frame rather than for the whole stack, because reading a frame's values costs a metadata
/// read and a PDB lookup each, and a reader looks at one frame at a time. <see cref="Symbols"/> is
/// carried here as well as on the frame so a caller holding only this answer can still tell a
/// compiler temporary from a missing PDB.
/// </para>
/// </summary>
public sealed record LiveFrameVariables : LiveExecutionReport
{
	/// <summary>The frame these came from, as indexed by the stack this was read against.</summary>
	public required int FrameIndex { get; init; }

	public int? ThreadId { get; init; }

	/// <summary>The method the frame is in, so a caller can check it is reading the frame it meant.</summary>
	public string? MethodFullName { get; init; }

	/// <summary>Whether symbols named these locals, or slots did.</summary>
	public required LiveSymbolState Symbols { get; init; }

	public IReadOnlyList<LiveVariable> Variables { get; init; } = [];

	/// <summary>Whether the frame has more variables than were reported.</summary>
	public required bool Truncated { get; init; }
}
