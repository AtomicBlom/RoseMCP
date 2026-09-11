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
}
