using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a reader looks at while the target is held: the frames, which one is selected, and its
/// source with the line execution is on marked.
/// <para>
/// The highlight is what a step is watched through, so the assertion that it lands on the selected
/// frame's own line -- not the innermost one -- is the one that matters. Clicking up the stack and
/// seeing the caller's code with the callee's line lit would be a confident wrong answer about where
/// the target is.
/// </para>
/// </summary>
public sealed class StopInspectionTests
{
	[Test]
	public void A_stack_opens_on_the_frame_the_debugger_is_holding()
	{
		var stop = new StopInspection(Stack(
			Frame(0, "MyApp.Widget.Refresh", line: 40, isActive: false),
			Frame(1, "MyApp.Widget.Run", line: 12, isActive: true)));

		Assert.Equal(2, stop.Frames.Count);
		Assert.Equal("MyApp.Widget.Run", stop.Selected!.Method);
		Assert.Equal(77, stop.StopSequence);
		Assert.Equal(9, stop.ThreadId);
	}

	/// <summary>
	/// A stack with no active frame still opens somewhere. An empty right-hand pane beside a list of
	/// frames reads as a pane that failed to load.
	/// </summary>
	[Test]
	public void A_stack_with_nothing_marked_active_opens_on_the_innermost_frame()
	{
		var stop = new StopInspection(Stack(Frame(0, "MyApp.Widget.Refresh", line: 40, isActive: false)));

		Assert.Equal("MyApp.Widget.Refresh", stop.Selected!.Method);
	}

	[Test]
	public void The_selected_frames_line_is_the_one_marked_in_its_source()
	{
		var stop = new StopInspection(Stack(Frame(0, "MyApp.Widget.Refresh", line: 41, isActive: true)));

		stop.Show(stop.Selected!, Source(firstLine: 40, "var a = 1;", "var b = 2;", "return a + b;"));

		Assert.Equal([false, true, false], stop.Source.Select(row => row.IsCurrent));
		Assert.False(stop.HasSourceDetail);
	}

	/// <summary>
	/// A reader clicking down a stack starts a read per frame, and they do not come back in order.
	/// Without this the last answer to arrive wins rather than the last frame asked for, and the
	/// source on screen is a frame nobody has selected.
	/// </summary>
	[Test]
	public void Source_read_for_a_frame_that_is_no_longer_selected_is_dropped()
	{
		var stop = new StopInspection(Stack(
			Frame(0, "MyApp.Widget.Refresh", line: 41, isActive: true),
			Frame(1, "MyApp.Widget.Run", line: 12, isActive: false)));

		var inner = stop.Selected!;
		stop.Selected = stop.Frames[1];

		stop.Show(inner, Source(firstLine: 40, "var a = 1;", "var b = 2;"));

		Assert.Empty(stop.Source);
	}

	[Test]
	public void A_frame_with_no_source_says_why_rather_than_showing_an_empty_listing()
	{
		var stop = new StopInspection(Stack(Frame(0, "System.Private.CoreLib.Thread.Sleep", line: null, isActive: true)));

		stop.ShowNothing("System.Private.CoreLib has no symbols on this machine.");

		Assert.Empty(stop.Source);
		Assert.True(stop.HasSourceDetail);
		Assert.Contains("no symbols", stop.SourceDetail);
	}

	private static LiveStackFrames Stack(params LiveStackFrame[] frames) => new()
	{
		Execution = LiveExecutionState.StoppedAtBreakpoint,
		Stop = new LiveStop
		{
			State = LiveExecutionState.StoppedAtBreakpoint,
			EventSequence = 77,
			StoppedAtUtc = DateTime.UnixEpoch,
			Resume = LiveStopResume.AutoContinue,
			ResumeDeadlineUtc = DateTime.UnixEpoch.AddSeconds(30),
		},
		ThreadId = 9,
		Frames = frames,
		Offset = 0,
		Total = frames.Length,
		Truncated = false,
	};

	private static LiveStackFrame Frame(int index, string method, int? line, bool isActive) => new()
	{
		Index = index,
		ThreadId = 9,
		Module = "MyApp.dll",
		MethodFullName = method,
		Location = $"MyApp!{method}",
		IlOffset = 0x14,
		Mapping = LiveIlMapping.Exact,
		Symbols = line is null ? LiveSymbolState.NoSymbols : LiveSymbolState.Resolved,
		Source = line is { } at
			? new LiveSourcePosition { File = @"C:\build\Widget.cs", Line = at, Column = 3, EndLine = at, EndColumn = 20 }
			: null,
		IsActive = isActive,
		SkippedBefore = 0,
	};

	private static LiveMethodSource Source(int firstLine, params string[] lines) => new()
	{
		Location = "MyApp!MyApp.Widget.Refresh",
		DisplayName = "Widget.Refresh",
		Module = "MyApp",
		Symbols = LiveSymbolState.Resolved,
		File = @"C:\build\Widget.cs",
		FirstLine = firstLine,
		Lines = lines,
		Positions = [],
	};
}
