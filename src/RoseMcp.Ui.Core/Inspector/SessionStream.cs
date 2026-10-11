using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One debug session's stream, as the Inspector shows it beside the code: the app's events, the agents' calls into
/// the session and the person's notes with the answers to them, interleaved in the order they happened, and filtered
/// to what the person chose to see.
/// <para>
/// Three sources feed it -- the host's event tail, the activity record narrowed to this session, and the notes about
/// something in it -- and each is merged here rather than shown apart, because the question it answers is what the
/// agent did to the app and what the app did about it, which no one of them says alone. The filter is applied here
/// to what was collected rather than by the host as events arrive, so changing it shows the past too.
/// </para>
/// </summary>
public sealed class SessionStream : Observable
{
	/// <summary>
	/// How many rows are kept. A session left open for an hour against a chatty target produces hundreds of thousands
	/// of events, and a list that holds all of them is a window that stops scrolling. The oldest go, because the
	/// reason anyone is watching is what just happened.
	/// </summary>
	public const int MaxRows = 2000;

	private readonly List<StreamRow> _all = [];
	private readonly Dictionary<string, StreamRow> _byKey = new(StringComparer.Ordinal);

	// The keys of calls, notes and answers trimmed lately, oldest first: a read can bring one again, and a key
	// remembered is what stops it being taken back in as new. Bounded, since a session watched for a day trims tens of
	// thousands, and a page brings at most the newest few hundred again.
	private readonly Queue<string> _goneOrder = new();
	private readonly HashSet<string> _gone = new(StringComparer.Ordinal);
	private StreamFilter _filter;
	private long _order;
	private long _cursor;
	private long _observed;
	private long _hostDropped;
	private long _trimmed;
	private string _notice = string.Empty;
	private DateTimeOffset? _exitedUtc;

	public SessionStream(string sessionId)
	{
		SessionId = sessionId;
	}

	/// <summary>The debug session this is the stream of.</summary>
	public string SessionId { get; }

	/// <summary>What is shown: every row <see cref="Filter"/> matches, oldest first.</summary>
	public ObservableCollection<StreamRow> Rows { get; } = [];

	/// <summary>What the person chose to see. Changing it shows everything collected that matches, the past included.</summary>
	public StreamFilter Filter
	{
		get => _filter;
		set
		{
			if (_filter == value) return;

			Set(ref _filter, value);
			Rows.Clear();
			foreach (var row in _all.Where(row => row.Matches(value))) Rows.Add(row);

			OnPropertyChanged(nameof(IsEmpty));
			OnPropertyChanged(nameof(EmptyText));
		}
	}

	/// <summary>Where the event tail has read to, passed back as <c>after</c> on the next poll.</summary>
	public long Cursor
	{
		get => _cursor;
		private set => Set(ref _cursor, value);
	}

	/// <summary>Whether nothing is shown.</summary>
	public bool IsEmpty => Rows.Count == 0;

	/// <summary>What an empty stream says: nothing yet, or nothing under this filter.</summary>
	public string EmptyText => _all.Count == 0
		? "Nothing yet. The app's events, the agents' calls into this session and your notes arrive here as they happen."
		: "Nothing of this kind yet. Choose All to see everything.";

	/// <summary>
	/// What the stream is not showing: events the host's buffer dropped before this reader got to them, and older rows
	/// trimmed here. Said rather than left to be inferred from a gap, which nobody reads.
	/// </summary>
	public string Notice
	{
		get => _notice;
		private set
		{
			Set(ref _notice, value);
			OnPropertyChanged(nameof(HasNotice));
		}
	}

	public bool HasNotice => Notice.Length > 0;

	/// <summary>When the app's session recorded it exiting, or null where it has not.</summary>
	public DateTimeOffset? ExitedUtc
	{
		get => _exitedUtc;
		private set => Set(ref _exitedUtc, value);
	}

	/// <summary>
	/// A watch on this stream's event tail: each read asks from <see cref="Cursor"/>, the first waiting for nothing and the
	/// rest held open by the host until an event lands. It is a <see cref="ViewWatch{T}"/> so a read that fails pauses
	/// for <paramref name="retryAfter"/> before the next -- a broker that is not there refuses at once, and without the
	/// pause a closed port is a core spinning for as long as the window stays open -- and a kick ends the pause.
	/// </summary>
	/// <param name="read">Reads a page of the tail: after a cursor, waiting up to so many seconds.</param>
	/// <param name="onPage">Given each page read, on the context the watch was started from.</param>
	/// <param name="onFailure">Given each read that failed, before the pause.</param>
	/// <param name="retryAfter">How long a failed read pauses; <see cref="ViewWatch{T}.RetryAfter"/> where null.</param>
	public ViewWatch<LiveDebugEventPage> Tail(
		Func<long, int, CancellationToken, Task<LiveDebugEventPage>> read,
		Action<LiveDebugEventPage> onPage,
		Action<Exception> onFailure,
		TimeSpan? retryAfter = null) =>
		new(
			(_, wait, cancellationToken) => read(Cursor, wait, cancellationToken),
			page => page.NextCursor.ToString(CultureInfo.InvariantCulture),
			onPage,
			onFailure,
			retryAfter);

	/// <summary>
	/// Takes a page of the event tail, advancing the cursor and counting what the host dropped between the last read
	/// and this one.
	/// </summary>
	public void Absorb(LiveDebugEventPage page)
	{
		// The host's buffer is bounded, so a reader that fell behind has a cursor older than what is still there. The
		// gap is real events nobody will ever see, and the count of them is what says whether the stream is complete.
		if (_observed > 0 && page.OldestAvailable > Cursor + 1) _hostDropped += page.OldestAvailable - (Cursor + 1);

		foreach (var entry in page.Events)
		{
			var row = StreamRow.Of(SessionId, new EventRow(entry));
			if (_byKey.ContainsKey(row.Key)) continue;

			Insert(row);
			if (entry.Kind == LiveDebugEventKind.ProcessExited) ExitedUtc = row.TimeUtc;
		}

		Cursor = page.NextCursor;
		_observed = page.TotalObserved;
		Trim();
	}

	/// <summary>
	/// Takes a page of the activity record: each call into this session is added once and updated in place as it ends.
	/// A call to another session is passed over, whatever the page was asked for.
	/// </summary>
	public void Absorb(ActivityPage page)
	{
		var calls = page.Entries
			.Where(entry => entry.Kind == ActivityEntryKind.Call && string.Equals(entry.DebugSession, SessionId, StringComparison.Ordinal))
			.OrderBy(entry => entry.StartedUtc);

		foreach (var entry in calls)
		{
			if (_byKey.TryGetValue($"c:{entry.Id}", out var known)) known.Update(entry);
			else if (!_gone.Contains($"c:{entry.Id}")) Insert(StreamRow.Of(entry, ++_order));
		}

		Trim();
	}

	/// <summary>
	/// Takes the notes the broker holds: each one about something in this session is added once and updated as its
	/// delivery moves, and each answer to one is added after it.
	/// </summary>
	public void Absorb(NoteList notes)
	{
		foreach (var note in notes.Notes.Where(note => string.Equals(note.Subject.Session, SessionId, StringComparison.Ordinal)))
		{
			if (_byKey.TryGetValue($"n:{note.Id}", out var known)) known.Update(note);
			else if (!_gone.Contains($"n:{note.Id}")) Insert(StreamRow.Of(note, ++_order));

			for (var index = 0; index < note.Replies.Count; index++)
			{
				var key = $"n:{note.Id}:r:{index}";
				if (_byKey.ContainsKey(key) || _gone.Contains(key)) continue;

				Insert(StreamRow.Of(note, note.Replies[index], index, ++_order));
			}
		}

		Trim();
	}

	/// <summary>Puts a row where its time puts it, in what is kept and, if the filter shows it, in what is shown.</summary>
	private void Insert(StreamRow row)
	{
		_all.Insert(Position(_all, row), row);
		_byKey[row.Key] = row;

		if (row.Matches(Filter)) Rows.Insert(Position(Rows, row), row);

		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(EmptyText));
	}

	/// <summary>
	/// Where <paramref name="row"/> goes in <paramref name="rows"/>: after every row that happened before it, and after
	/// any at the same instant that was taken first. Almost always the end, so it is looked for from there.
	/// </summary>
	private static int Position(IList<StreamRow> rows, StreamRow row)
	{
		var index = rows.Count;
		while (index > 0 && Before(row, rows[index - 1])) index--;

		return index;
	}

	private static bool Before(StreamRow row, StreamRow other) =>
		row.TimeUtc < other.TimeUtc || (row.TimeUtc == other.TimeUtc && row.Kind == other.Kind && row.Order < other.Order);

	/// <summary>Drops the oldest rows past <see cref="MaxRows"/>, counting them, and says what the stream is not showing.</summary>
	private void Trim()
	{
		while (_all.Count > MaxRows)
		{
			var oldest = _all[0];
			_all.RemoveAt(0);
			Rows.Remove(oldest);

			_byKey.Remove(oldest.Key);

			// An event is never read twice, the cursor having passed it. A call or a note can be in the next page again.
			if (oldest.Kind != StreamKind.Event) Forget(oldest.Key);
			_trimmed++;
		}

		var parts = new List<string>();
		if (_hostDropped > 0) parts.Add(InspectorText.DroppedEvents(_hostDropped));
		if (_trimmed > 0) parts.Add(_trimmed == 1 ? "1 older row was trimmed here." : $"{_trimmed} older rows were trimmed here.");

		Notice = string.Join(" ", parts);
		OnPropertyChanged(nameof(IsEmpty));
	}

	/// <summary>
	/// How many trimmed calls, notes and answers the stream remembers so a read bringing one again does not take it back
	/// in. More than a read of the record or the notes ever brings back at once.
	/// </summary>
	public const int RememberedTrimmed = 500;

	/// <summary>How many rows the stream can tell it has seen: those it holds and the trimmed ones it remembers. Bounded.</summary>
	public int Known => _byKey.Count + _gone.Count;

	/// <summary>Remembers a trimmed row's key, forgetting the oldest past <see cref="RememberedTrimmed"/>.</summary>
	private void Forget(string key)
	{
		if (!_gone.Add(key)) return;

		_goneOrder.Enqueue(key);
		while (_goneOrder.Count > RememberedTrimmed) _gone.Remove(_goneOrder.Dequeue());
	}
}
