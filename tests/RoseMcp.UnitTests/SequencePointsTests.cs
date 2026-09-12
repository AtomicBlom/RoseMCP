using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which line an IL offset came from, given a method's sequence points.
/// <para>
/// Hand-written points rather than a real PDB, because the rule has three edges and a compiled
/// method exercises whichever ones its own code happens to produce. Each edge is a way of naming the
/// wrong line confidently, which is the failure mode this whole reader exists to avoid.
/// </para>
/// </summary>
public sealed class SequencePointsTests
{
	private static SequencePointInfo At(int offset, int line) => new()
	{
		Offset = offset,
		IsHidden = false,
		Position = new SourcePosition { File = "P.cs", Line = line, Column = 1, EndLine = line, EndColumn = 20 },
	};

	private static SequencePointInfo Hidden(int offset) => new() { Offset = offset, IsHidden = true };

	/// <summary>
	/// A sequence point covers every offset from itself to the next one, so an offset in the middle
	/// of a statement belongs to the statement that began before it. Rounding forward would report
	/// the line about to run rather than the one running.
	/// </summary>
	[Test]
	[Arguments(0, 10)]
	[Arguments(3, 10)]
	[Arguments(7, 20)]
	[Arguments(12, 20)]
	[Arguments(14, 30)]
	[Arguments(9000, 30)]
	public void An_offset_belongs_to_the_last_point_at_or_before_it(int ilOffset, int expectedLine)
	{
		var points = new[] { At(0, 10), At(7, 20), At(14, 30) };

		Assert.Equal(expectedLine, SequencePoints.Nearest(points, ilOffset)?.Line);
	}

	/// <summary>
	/// Nothing precedes the first point, so there is nothing to attribute the offset to. Answering
	/// with the first point instead would place a frame on a line whose code has not begun.
	/// </summary>
	[Test]
	public void An_offset_before_the_first_point_has_no_source()
	{
		var points = new[] { At(5, 10) };

		Assert.Null(SequencePoints.Nearest(points, 4));
		Assert.Null(SequencePoints.Nearest([], 0));
	}

	/// <summary>
	/// A hidden point is an answer, not a gap: it says the IL from here maps to no source. Skipping
	/// it to reach the last real point would attribute compiler-generated code -- an await's
	/// bookkeeping, an iterator's closing machinery -- to the line above it.
	/// </summary>
	[Test]
	public void A_hidden_point_owns_the_offsets_after_it()
	{
		var points = new[] { At(0, 10), Hidden(6), At(20, 30) };

		Assert.Equal(10, SequencePoints.Nearest(points, 5)?.Line);
		Assert.Null(SequencePoints.Nearest(points, 6));
		Assert.Null(SequencePoints.Nearest(points, 19));
		Assert.Equal(30, SequencePoints.Nearest(points, 20)?.Line);
	}

	/// <summary>The whole position is carried through, not only the line: a statement wrapped over
	/// three lines has one point covering all of it.</summary>
	[Test]
	public void The_answer_is_the_span_the_point_recorded()
	{
		var point = new SequencePointInfo
		{
			Offset = 0,
			IsHidden = false,
			Position = new SourcePosition { File = @"D:\build\P.cs", Line = 11, Column = 9, EndLine = 13, EndColumn = 42 },
		};

		var found = SequencePoints.Nearest([point], 4);

		Assert.NotNull(found);
		Assert.Equal(@"D:\build\P.cs", found!.File);
		Assert.Equal((11, 9, 13, 42), (found.Line, found.Column, found.EndLine, found.EndColumn));
	}
}
