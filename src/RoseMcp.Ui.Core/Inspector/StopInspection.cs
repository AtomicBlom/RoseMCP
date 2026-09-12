using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// What a reader is looking at while the target is held: one stop's frames, which of them is
/// selected, and the source of that one.
/// <para>
/// Replaced wholesale when the stop's sequence changes rather than updated in place. Everything a
/// debugger hands out is valid only within the stop it came from -- a frame index, a value path, a
/// thread's position -- so carrying any of it across would be a row that reads correctly and
/// describes somewhere the target no longer is. A new stop is a new object, and nothing can be left
/// behind by accident.
/// </para>
/// </summary>
public sealed class StopInspection : Observable
{
	private FrameRow? _selected;
	private IReadOnlyList<SourceLineRow> _source = [];
	private string _sourceDetail = string.Empty;
	private bool _hasSourceDetail;

	private string _variablesDetail = string.Empty;

	private bool _hasVariablesDetail;

	public StopInspection(LiveStackFrames frames)
	{
		StopSequence = frames.Stop?.EventSequence ?? 0;
		ThreadId = frames.ThreadId;
		Frames = [.. frames.Frames.Select(frame => new FrameRow(frame))];

		// The frame the debugger is holding, which is what an evaluation resolves names against and
		// where a reader wants to be looking when a stack first appears.
		_selected = Frames.FirstOrDefault(frame => frame.IsActive) ?? Frames.FirstOrDefault();
	}

	/// <summary>
	/// The stop this belongs to. It is the stop's identity, so a sequence that has moved means the
	/// target stopped again somewhere else and everything here describes the last one.
	/// </summary>
	public long StopSequence { get; }

	/// <summary>The thread whose stack this is.</summary>
	public int? ThreadId { get; }

	/// <summary>The frames, innermost first.</summary>
	public ObservableCollection<FrameRow> Frames { get; }

	/// <summary>The frame whose source and variables are being shown.</summary>
	public FrameRow? Selected
	{
		get => _selected;
		set => Set(ref _selected, value);
	}

	/// <summary>
	/// The selected frame's source, with the line execution is on marked. Empty until it has been
	/// read, and empty for good when the frame's module has no source on this machine.
	/// </summary>
	public IReadOnlyList<SourceLineRow> Source
	{
		get => _source;
		private set => Set(ref _source, value);
	}

	/// <summary>Why there is no source to show, when there is none.</summary>
	public string SourceDetail
	{
		get => _sourceDetail;
		private set => Set(ref _sourceDetail, value);
	}

	public bool HasSourceDetail
	{
		get => _hasSourceDetail;
		private set => Set(ref _hasSourceDetail, value);
	}

	/// <summary>
	/// Takes the source read for the selected frame, marking the line it is stopped on.
	/// <para>
	/// Ignored when the frame it was read for is no longer the selected one. A read is a round trip
	/// and a reader clicking down a stack starts several, so without this the last one to arrive
	/// wins rather than the last one asked for.
	/// </para>
	/// </summary>
	public void Show(FrameRow frame, LiveMethodSource source)
	{
		if (!ReferenceEquals(frame, Selected)) return;

		Source = SourceView.Build(source, frame.Line);
		SourceDetail = source.Detail ?? string.Empty;
		HasSourceDetail = SourceDetail.Length > 0;
	}

	/// <summary>Forgets the shown source, for a frame that cannot be read at all.</summary>
	public void ShowNothing(string detail)
	{
		Source = [];
		SourceDetail = detail;
		HasSourceDetail = detail.Length > 0;
	}

	/// <summary>
	/// The selected frame's arguments and locals. Replaced when the selection moves, and empty until
	/// they have been read.
	/// </summary>
	public ObservableCollection<VariableNode> Variables { get; } = [];

	/// <summary>What the host said about the variables that the list does not. Empty when nothing.</summary>
	public string VariablesDetail
	{
		get => _variablesDetail;
		private set => Set(ref _variablesDetail, value);
	}

	public bool HasVariablesDetail
	{
		get => _hasVariablesDetail;
		private set => Set(ref _hasVariablesDetail, value);
	}

	/// <summary>
	/// Takes the variables read for the selected frame.
	/// <para>
	/// Dropped when the frame they were read for is no longer the selected one, for the same reason
	/// the source is: clicking down a stack starts a read per frame and they do not come back in the
	/// order they were asked for.
	/// </para>
	/// </summary>
	public void Show(FrameRow frame, LiveFrameVariables variables)
	{
		if (!ReferenceEquals(frame, Selected)) return;

		Variables.Clear();
		foreach (var variable in variables.Variables)
		{
			Variables.Add(new VariableNode(variable));
		}

		VariablesDetail = DescribeVariables(variables);
		HasVariablesDetail = VariablesDetail.Length > 0;
	}

	/// <summary>Forgets the variables, for a frame whose values cannot be read at all.</summary>
	public void ShowNoVariables(string detail)
	{
		Variables.Clear();
		VariablesDetail = detail;
		HasVariablesDetail = detail.Length > 0;
	}

	/// <summary>
	/// What a list of variables does not say for itself. Symbols are the one worth a caption: without
	/// them every local is <c>local_0</c> upwards by slot, which reads as the code having no names
	/// rather than as this machine not having the PDB.
	/// </summary>
	private static string DescribeVariables(LiveFrameVariables variables)
	{
		if (variables.Detail is { Length: > 0 } detail) return detail;

		var parts = new List<string>();

		if (variables.Truncated) parts.Add("more values than are shown");

		if (variables.Symbols == LiveSymbolState.NoSymbols)
		{
			parts.Add("no symbols for this module, so locals are numbered by slot rather than named");
		}
		else if (variables.Symbols == LiveSymbolState.SymbolsMismatched)
		{
			parts.Add("the symbols belong to another build, so they are refused and locals are numbered by slot");
		}

		return string.Join(Format.Separator, parts);
	}
}
