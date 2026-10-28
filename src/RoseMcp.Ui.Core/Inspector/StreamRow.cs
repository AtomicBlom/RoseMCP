using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>What a row of the session stream is: something the app did, a call an agent made, a note, or an answer to one.</summary>
public enum StreamKind
{
	/// <summary>An event the debugged app's session recorded: a stop, an exception, a log line, a module.</summary>
	Event,

	/// <summary>A call an agent session made into this debug session, from the activity record.</summary>
	Call,

	/// <summary>A note the person sent an agent session about something in this debug session.</summary>
	Note,

	/// <summary>What the agent answered to such a note.</summary>
	Reply,
}

/// <summary>Which of the stream's choices of what to show a row belongs to; every row is in <see cref="All"/>.</summary>
public enum StreamFilter
{
	/// <summary>Everything.</summary>
	All,

	/// <summary>The agents' calls into this session and their answers to notes.</summary>
	Agent,

	/// <summary>Breakpoint hits, completed steps and pauses.</summary>
	Stops,

	/// <summary>First-chance and unhandled exceptions.</summary>
	Exceptions,

	/// <summary>What the app logged, and what its tracepoints logged.</summary>
	Log,

	/// <summary>The person's notes and the agents' answers.</summary>
	Notes,
}

/// <summary>Which colour a row's "who" is drawn in.</summary>
public enum StreamTone
{
	/// <summary>The app's own ordinary traffic.</summary>
	App,

	/// <summary>A stop.</summary>
	Stop,

	/// <summary>An exception, an exit, or a call that failed.</summary>
	Critical,

	/// <summary>An agent session.</summary>
	Agent,

	/// <summary>The person.</summary>
	You,
}

/// <summary>
/// One line of the session stream: when, who, what, and what more there is to say about it.
/// <para>
/// A call is updated in place as the record says it ended, and a note as its delivery moves; an event, like the
/// host's record of it, never changes. Every row keeps the time it happened so the stream can interleave what four
/// sources said into the order it happened in.
/// </para>
/// </summary>
public sealed class StreamRow : Observable
{
	private string _text;
	private string _detail = string.Empty;
	private StreamTone _tone;
	private NoteSubject? _subject;

	private StreamRow(string key, StreamKind kind, DateTimeOffset timeUtc, long order, string who, StreamTone tone, string text)
	{
		Key = key;
		Kind = kind;
		TimeUtc = timeUtc;
		Order = order;
		Time = timeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		Who = who;
		_tone = tone;
		_text = text;
	}

	/// <summary>What identifies the row across reads: its source and that source's own id.</summary>
	public string Key { get; }

	public StreamKind Kind { get; }

	/// <summary>When it happened, which orders the stream.</summary>
	public DateTimeOffset TimeUtc { get; }

	/// <summary>Which of two rows at the same instant came first, in the order they were taken.</summary>
	public long Order { get; }

	/// <summary>Local time to the second.</summary>
	public string Time { get; }

	/// <summary>Who did it: the app, an agent session by name, you.</summary>
	public string Who { get; }

	public StreamTone Tone
	{
		get => _tone;
		private set => Set(ref _tone, value);
	}

	/// <summary>What happened, one line: an event's message, a call's tool and target, a note's text.</summary>
	public string Text
	{
		get => _text;
		private set => Set(ref _text, value);
	}

	/// <summary>What more there is to say: a call that failed and why, where a note's delivery has got to.</summary>
	public string Detail
	{
		get => _detail;
		private set
		{
			Set(ref _detail, value);
			OnPropertyChanged(nameof(HasDetail));
		}
	}

	public bool HasDetail => Detail.Length > 0;

	/// <summary>Whether the text is code-like -- a call, a log line -- and set in the mono face.</summary>
	public bool IsMono => Kind == StreamKind.Call || Event?.Kind is LiveDebugEventKind.LogMessage or LiveDebugEventKind.TracepointHit;

	/// <summary>The event, for one that came from the app, which carries the frames and values behind its expander.</summary>
	public EventRow? Event { get; private init; }

	/// <summary>Whether there are frames or values behind the row.</summary>
	public bool HasEventDetail => Event?.HasDetail == true;

	/// <summary>The note this row is, or answers, by its id.</summary>
	public string? NoteId { get; private init; }

	/// <summary>What a note to an agent about this row is about; null for a note or an answer, which are already to one.</summary>
	public NoteSubject? Subject
	{
		get => _subject;
		private set
		{
			Set(ref _subject, value);
			OnPropertyChanged(nameof(CanAsk));
		}
	}

	/// <summary>Whether an agent can be asked about this row.</summary>
	public bool CanAsk => Subject is not null;

	/// <summary>Whether the row is shown under <paramref name="filter"/>.</summary>
	public bool Matches(StreamFilter filter) => filter switch
	{
		StreamFilter.Agent => Kind is StreamKind.Call or StreamKind.Reply,
		StreamFilter.Notes => Kind is StreamKind.Note or StreamKind.Reply,
		StreamFilter.Stops => Event?.Kind is LiveDebugEventKind.BreakpointHit or LiveDebugEventKind.StepComplete or LiveDebugEventKind.Paused,
		StreamFilter.Exceptions => Event?.Kind is LiveDebugEventKind.ExceptionFirstChance or LiveDebugEventKind.ExceptionUnhandled,
		StreamFilter.Log => Event?.Kind is LiveDebugEventKind.LogMessage or LiveDebugEventKind.TracepointHit,
		_ => true,
	};

	/// <summary>An event the app's session recorded.</summary>
	public static StreamRow Of(string sessionId, EventRow entry)
	{
		var at = new DateTimeOffset(DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc));
		// The thread is added only where the host's message does not already name it, which most stops and exceptions do.
		var named = entry.HasThread && entry.Message.Contains(entry.Thread, StringComparison.Ordinal);
		var text = entry.HasThread && !named ? $"{entry.Message} ({entry.Thread})" : entry.Message;

		return new StreamRow($"e:{entry.Sequence}", StreamKind.Event, at, entry.Sequence, WhoOf(entry.Kind), ToneOf(entry.Kind), text)
		{
			Event = entry,
			Subject = new NoteSubject
			{
				Kind = NoteSubjectKind.DebugEvent,
				Readable = $"{entry.KindLabel}: {entry.Message}",
				Session = sessionId,
				EventSequence = entry.Sequence,
			},
			Detail = entry.ExceptionType,
		};
	}

	/// <summary>A call an agent made into the session.</summary>
	public static StreamRow Of(ActivityEntry entry, long order)
	{
		var row = new StreamRow($"c:{entry.Id}", StreamKind.Call, entry.StartedUtc, order, AgentNames.Of(entry), StreamTone.Agent, string.Empty);
		row.Update(entry);

		return row;
	}

	/// <summary>A note the person sent about something in the session.</summary>
	public static StreamRow Of(AgentNote note, long order)
	{
		var row = new StreamRow($"n:{note.Id}", StreamKind.Note, note.PostedUtc, order, $"You → {AgentNames.Of(note)}", StreamTone.You, note.Text)
		{
			NoteId = note.Id,
		};
		row.Update(note);

		return row;
	}

	/// <summary>The agent's answer to a note.</summary>
	public static StreamRow Of(AgentNote note, NoteReply reply, int index, long order) =>
		new($"n:{note.Id}:r:{index}", StreamKind.Reply, reply.RepliedUtc, order, $"{AgentNames.Of(note)} replied", StreamTone.Agent, reply.Text)
		{
			NoteId = note.Id,
		};

	/// <summary>Brings a call's row in line with the record: how it ended, and what it is about for a note.</summary>
	public void Update(ActivityEntry entry)
	{
		Text = string.Join(" ", new[] { entry.Tool, entry.Target }.Where(part => part is { Length: > 0 }));
		Tone = entry.Outcome == ActivityOutcome.Failed ? StreamTone.Critical : StreamTone.Agent;
		Detail = entry.Outcome switch
		{
			ActivityOutcome.Running => "running",
			ActivityOutcome.Failed => entry.Error is { Length: > 0 } error ? $"failed: {error}" : "failed",
			ActivityOutcome.Cancelled => "cancelled",
			_ => string.Empty,
		};
		Subject = OperationRow.SubjectOf(entry);
	}

	/// <summary>Brings a note's row in line with where its delivery has got to.</summary>
	public void Update(AgentNote note) => Detail = $"about {note.Subject.Readable}{Format.Separator}{NoteRow.StateOf(note)}";

	private static string WhoOf(LiveDebugEventKind kind) => kind switch
	{
		LiveDebugEventKind.BreakpointHit or LiveDebugEventKind.StepComplete => "Stopped",
		LiveDebugEventKind.Paused => "Paused",
		LiveDebugEventKind.TracepointHit => "Tracepoint",
		LiveDebugEventKind.ExceptionFirstChance => "Exception",
		LiveDebugEventKind.ExceptionUnhandled => "Unhandled",
		LiveDebugEventKind.LogMessage => "Log",
		LiveDebugEventKind.ProcessExited => "Exited",
		LiveDebugEventKind.SessionNotice => "Session",
		_ => "App",
	};

	private static StreamTone ToneOf(LiveDebugEventKind kind) => EventRow.ToneOf(kind) switch
	{
		EventRow.EventTone.Critical or EventRow.EventTone.Caution => StreamTone.Critical,
		EventRow.EventTone.Notable => StreamTone.Stop,
		_ => StreamTone.App,
	};
}
