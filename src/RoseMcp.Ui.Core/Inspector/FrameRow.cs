using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One frame of a stopped call stack as the inspector shows it.
/// <para>
/// Built fresh per stop rather than updated in place, unlike a breakpoint row. A frame belongs to
/// the stop it was read at and every handle in it dies with that stop, so carrying one across would
/// be a row describing somewhere the target no longer is.
/// </para>
/// </summary>
public sealed class FrameRow
{
	public FrameRow(LiveStackFrame frame)
	{
		Index = frame.Index;
		IndexLabel = frame.Index.ToString();
		Method = frame.MethodFullName ?? "(a frame metadata could not name)";
		Module = frame.Module ?? string.Empty;
		Location = frame.Location;
		Line = frame.Source?.Line;
		IsActive = frame.IsActive;

		Where = Describe(frame);
		HasWhere = Where.Length > 0;
		Note = Warn(frame);
		HasNote = Note.Length > 0;
	}

	/// <summary>Position on the stack, which is how this frame's variables are asked for.</summary>
	public int Index { get; }

	public string IndexLabel { get; }

	/// <summary>The method, as <c>Namespace.Type.Method</c>.</summary>
	public string Method { get; }

	/// <summary>The module's file name.</summary>
	public string Module { get; }

	/// <summary>
	/// The method in the spelling that reads its source, or null when metadata could not name it.
	/// A frame with no location cannot be shown, only listed.
	/// </summary>
	public string? Location { get; }

	/// <summary>The line execution is on inside this frame, or null when symbols could not say.</summary>
	public int? Line { get; }

	/// <summary>Whether this is the frame the debugger is holding.</summary>
	public bool IsActive { get; }

	/// <summary>The file, line and instruction offset, in one line.</summary>
	public string Where { get; }

	public bool HasWhere { get; }

	/// <summary>
	/// What is not quite right about this frame: instructions that map to their line only
	/// approximately, symbols that are absent or from another build, frames underneath it that could
	/// not be represented at all. Empty when there is nothing to say.
	/// </summary>
	public string Note { get; }

	public bool HasNote { get; }

	/// <summary>Where the frame is, as a reader reads it: <c>Program.cs:67 · IL_0014</c>.</summary>
	private static string Describe(LiveStackFrame frame)
	{
		var parts = new List<string>();

		if (Format.FileLine(frame.Source?.File, frame.Source?.Line) is { Length: > 0 } at) parts.Add(at);
		if (frame.IlOffset is { } offset) parts.Add(Format.IlOffset(offset));

		return string.Join(" · ", parts);
	}

	/// <summary>
	/// The caveats on a frame, said rather than left to be inferred from an absence.
	/// <para>
	/// A stack with three frames quietly missing reads as a complete stack with a surprising caller,
	/// and a line that is only approximately where execution is reads as one that is exactly there.
	/// Both are confident wrong answers, which is the shape worth spending a caption on.
	/// </para>
	/// </summary>
	private static string Warn(LiveStackFrame frame)
	{
		var parts = new List<string>();

		if (frame.SkippedBefore > 0)
		{
			parts.Add($"{Format.Count(frame.SkippedBefore, "frame")} below this one could not be read");
		}

		if (frame.Mapping != LiveIlMapping.Exact) parts.Add($"mapping: {frame.Mapping}".ToLowerInvariant());

		if (frame.Symbols != LiveSymbolState.Resolved && frame.Symbols != LiveSymbolState.NoSequencePoint)
		{
			parts.Add(frame.Symbols == LiveSymbolState.SymbolsMismatched
				? "symbols belong to another build of this module"
				: "no symbols for this module");
		}

		return string.Join(" · ", parts);
	}
}
