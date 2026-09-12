namespace RoseMcp.Symbols;

/// <summary>
/// Which source position an IL offset came from, given a method's sequence points.
/// <para>
/// Pure and separate from the reader so it can be tested against hand-written points. The rule is
/// short and every part of it is load-bearing, which is exactly the shape that deserves a test
/// rather than a comment.
/// </para>
/// </summary>
public static class SequencePoints
{
	/// <summary>
	/// The point an offset belongs to: the last one at or before it.
	/// <para>
	/// Not the nearest, and not the first at or after. A sequence point marks where a statement's IL
	/// begins and covers everything until the next point, so an offset in the middle of a statement
	/// belongs to the statement that started before it. Rounding forward would report the line the
	/// execution is about to reach rather than the one it is on.
	/// </para>
	/// <para>
	/// A hidden point is a real answer of "no source here", so it stops the search rather than being
	/// skipped over: skipping would attribute compiler-generated IL to the last line of real code
	/// before it, which is a confident wrong answer rather than an absent one. Null means the offset
	/// has no source, either because it precedes every point or because a hidden one owns it.
	/// </para>
	/// </summary>
	public static SourcePosition? Nearest(IReadOnlyList<SequencePointInfo> points, int ilOffset)
	{
		SequencePointInfo? owning = null;

		foreach (var point in points)
		{
			if (point.Offset > ilOffset) break;

			owning = point;
		}

		return owning?.IsHidden == false ? owning.Position : null;
	}
}
