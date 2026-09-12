using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// One frame of a stopped call stack, as the inspector shows it.
/// <para>
/// The caveats are most of what is asserted, because a stack is read as a complete account of how
/// the target got where it is. Three frames quietly missing read as a surprising caller, and a line
/// that is only approximately where execution is reads as one that is exactly there -- both are
/// confident wrong answers about somebody's bug.
/// </para>
/// </summary>
public sealed class FrameRowTests
{
	[Test]
	public void A_frame_reads_as_its_file_line_and_offset()
	{
		var row = new FrameRow(Frame(
			method: "MyApp.Widget.Refresh",
			source: new LiveSourcePosition
			{
				File = @"D:\repo\src\Widget.cs",
				Line = 42,
				Column = 3,
				EndLine = 42,
				EndColumn = 40,
			},
			ilOffset: 20));

		Assert.Equal("MyApp.Widget.Refresh", row.Method);
		Assert.Equal("Widget.cs:42 · IL_0014", row.Where);
		Assert.True(row.HasWhere);
		Assert.Equal(42, row.Line);
	}

	/// <summary>
	/// A frame with no symbols still says where it is in the only terms it has. An empty caption
	/// would read as a frame the debugger could not walk, which is a different and worse claim.
	/// </summary>
	[Test]
	public void A_frame_with_no_source_still_gives_its_offset()
	{
		var row = new FrameRow(Frame("System.Threading.Monitor.Wait", ilOffset: 0, symbols: LiveSymbolState.NoSymbols));

		Assert.Equal("IL_0000", row.Where);
		Assert.Null(row.Line);
		Assert.Equal("no symbols for this module", row.Note);
	}

	/// <summary>
	/// A method metadata could not name is said to be one, rather than shown as a blank row. The
	/// frame is still on the stack, and leaving it out would change what the stack claims.
	/// </summary>
	[Test]
	public void A_frame_metadata_could_not_name_says_so()
	{
		var row = new FrameRow(Frame(method: null));

		Assert.Equal("(a frame metadata could not name)", row.Method);
		Assert.Null(row.Location);
	}

	/// <summary>
	/// The spelling that reads a method's source rides on the frame rather than being composed here.
	/// Splitting the full name at its last dot names a type that does not exist for a constructor,
	/// and gets a lambda right only by luck.
	/// </summary>
	[Test]
	public void The_location_is_taken_rather_than_composed()
	{
		var row = new FrameRow(Frame("MyApp.Widget..ctor", location: "MyApp!MyApp.Widget..ctor"));

		Assert.Equal("MyApp!MyApp.Widget..ctor", row.Location);
	}

	[Test]
	public void Frames_that_could_not_be_read_are_counted_in_the_note()
	{
		var row = new FrameRow(Frame("MyApp.Program.Main", skippedBefore: 3));

		Assert.Equal("3 frames below this one could not be read", row.Note);
		Assert.True(row.HasNote);
	}

	/// <summary>
	/// Everything wrong with a frame in one caption, because each of them alone is quiet enough to
	/// miss and together they are what explains a line that looks wrong.
	/// </summary>
	[Test]
	public void Every_caveat_is_said_at_once()
	{
		var row = new FrameRow(Frame(
			"MyApp.Widget.Refresh",
			skippedBefore: 1,
			mapping: LiveIlMapping.Approximate,
			symbols: LiveSymbolState.SymbolsMismatched));

		Assert.Equal(
			"1 frame below this one could not be read · mapping: approximate · symbols belong to another build of this module",
			row.Note);
	}

	/// <summary>
	/// A method with no sequence point at this instruction is not a caveat. It is ordinary inside
	/// compiler-generated code, and warning about it on every such frame would empty the word.
	/// </summary>
	[Test]
	public void An_instruction_between_sequence_points_is_not_a_complaint()
	{
		var row = new FrameRow(Frame("MyApp.Widget.Refresh", symbols: LiveSymbolState.NoSequencePoint));

		Assert.Equal(string.Empty, row.Note);
		Assert.False(row.HasNote);
	}

	private static LiveStackFrame Frame(
		string? method = "MyApp.Widget.Refresh",
		LiveSourcePosition? source = null,
		int? ilOffset = null,
		string? location = null,
		int skippedBefore = 0,
		LiveIlMapping mapping = LiveIlMapping.Exact,
		LiveSymbolState symbols = LiveSymbolState.Resolved) =>
		new()
		{
			Index = 0,
			ThreadId = 4128,
			Module = "MyApp.dll",
			MethodFullName = method,
			Location = location,
			IlOffset = ilOffset,
			Mapping = mapping,
			Symbols = symbols,
			Source = source,
			IsActive = true,
			SkippedBefore = skippedBefore,
		};
}
