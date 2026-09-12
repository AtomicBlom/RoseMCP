using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Everything the inspector holds about the one session it is showing: the event tail, the
/// breakpoints and tracepoints, and where the tail has read up to.
/// <para>
/// Per session rather than per window, so switching between two sessions and back does not throw
/// away a tail that took minutes to accumulate -- and so the cursor survives, which is what stops
/// the switch re-reading everything the host still has buffered.
/// </para>
/// </summary>
public sealed class InspectedSession : Observable
{
	/// <summary>
	/// How much tail is kept. A session left open for an hour against a chatty target produces
	/// hundreds of thousands of events, and a list that holds all of them is a window that stops
	/// scrolling. The oldest go, because the reason anyone is watching is what just happened.
	/// </summary>
	public const int MaxEvents = 2000;

	private long _cursor;
	private long _observed;
	private long _dropped;
	private bool _hasEvents;
	private string _notice = string.Empty;
	private bool _hasNotice;

	public InspectedSession(string sessionId)
	{
		SessionId = sessionId;
	}

	public string SessionId { get; }

	/// <summary>The tail, oldest first, capped at <see cref="MaxEvents"/>.</summary>
	public ObservableCollection<EventRow> Events { get; } = [];

	public ObservableCollection<BreakpointRow> Breakpoints { get; } = [];

	public ObservableCollection<TracepointRow> Tracepoints { get; } = [];

	/// <summary>
	/// The live app's visual tree, the element selected in it, and that element's properties.
	/// <para>
	/// Per session rather than per pane, for the reason the tail is: a handle is stable for the life of
	/// the element, so the expansion and the selection somebody built up are worth keeping across
	/// anything that rebinds the pane. A target with no XAML simply never has it read.
	/// </para>
	/// </summary>
	public XamlInspection Xaml { get; } = new();

	/// <summary>Where the tail has read to, passed back as <c>after</c> on the next poll.</summary>
	public long Cursor
	{
		get => _cursor;
		private set => Set(ref _cursor, value);
	}

	public bool HasEvents
	{
		get => _hasEvents;
		private set => Set(ref _hasEvents, value);
	}

	/// <summary>
	/// What the tail is not showing: events the host's buffer dropped before this reader got to
	/// them, or older ones this list has trimmed. Said rather than left to be inferred from a gap in
	/// the sequence numbers, which nobody reads.
	/// </summary>
	public string Notice
	{
		get => _notice;
		private set => Set(ref _notice, value);
	}

	public bool HasNotice
	{
		get => _hasNotice;
		private set => Set(ref _hasNotice, value);
	}

	/// <summary>
	/// Takes a page of events onto the end of the tail, advancing the cursor and noting anything the
	/// host dropped between the last read and this one.
	/// </summary>
	public void Absorb(LiveDebugEventPage page)
	{
		// The host's buffer is bounded, so a reader that fell behind has a cursor older than what is
		// still there. The gap is real events nobody will ever see, and the count of them is the one
		// thing that says whether the tail can be trusted as complete.
		if (_observed > 0 && page.OldestAvailable > Cursor + 1)
		{
			_dropped += page.OldestAvailable - (Cursor + 1);
		}

		foreach (var entry in page.Events)
		{
			Events.Add(new EventRow(entry));
		}

		while (Events.Count > MaxEvents)
		{
			_dropped++;
			Events.RemoveAt(0);
		}

		Cursor = page.NextCursor;
		_observed = page.TotalObserved;
		HasEvents = Events.Count > 0;
		Notice = _dropped > 0 ? InspectorText.DroppedEvents(_dropped) : string.Empty;
		HasNotice = Notice.Length > 0;
	}

	/// <summary>Merges the host's breakpoints in place, so a row a reader is about to click stays put.</summary>
	public void Absorb(LiveBreakpointList list) => Rows.Merge(
		Breakpoints,
		list.Breakpoints,
		row => row.Id,
		breakpoint => breakpoint.Id,
		breakpoint => new BreakpointRow(breakpoint),
		(row, breakpoint) => row.Update(breakpoint));

	/// <summary>The same for tracepoints.</summary>
	public void Absorb(LiveTracepointList list) => Rows.Merge(
		Tracepoints,
		list.Tracepoints,
		row => row.Id,
		tracepoint => tracepoint.Id,
		tracepoint => new TracepointRow(tracepoint),
		(row, tracepoint) => row.Update(tracepoint));
}
