namespace RoseMcp.Symbols;

/// <summary>Where one method's compiled code came from in source, and every point inside it.</summary>
public sealed record MethodExtent
{
	/// <summary>The method-def token.</summary>
	public required int MethodToken { get; init; }

	/// <summary>
	/// The method this one was written as, when it is a state machine the compiler emitted for an
	/// <c>async</c> method or an iterator; null for an ordinary method.
	/// <para>
	/// The PDB records it, which is the only reliable way to connect the two. An async method's own
	/// body compiles to almost nothing -- the code a reader sees belongs to a <c>MoveNext</c> on a
	/// generated type -- so without this link, asking to see an async method shows its signature and
	/// no body.
	/// </para>
	/// </summary>
	public required int? KickoffToken { get; init; }

	/// <summary>The source file, as the compiler recorded it: an absolute path on the build machine.</summary>
	public required string File { get; init; }

	/// <summary>The first line any of its code came from.</summary>
	public required int FirstLine { get; init; }

	/// <summary>The last line any of its code came from.</summary>
	public required int LastLine { get; init; }

	/// <summary>
	/// Its sequence points in IL order, hidden ones included. A hidden point is kept because it says
	/// where a stretch of IL stops belonging to any line, and a position picker that dropped them
	/// would offer the reader a line the compiler has disclaimed.
	/// </summary>
	public required IReadOnlyList<SequencePointInfo> Points { get; init; }
}
