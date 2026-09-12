using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One line of the event tail as the inspector shows it: when, what kind, on which thread, and the
/// frames and variables the host captured with it.
/// <para>
/// An event never changes once the host has recorded it, so this is built once and never updated --
/// unlike the rows a poll refreshes. That is what lets the tail hold two thousand of them and still
/// scroll: nothing is re-bound as new ones arrive.
/// </para>
/// </summary>
public sealed class EventRow
{
	public EventRow(LiveDebugEvent entry)
	{
		Sequence = entry.Sequence;
		Kind = entry.Kind;
		KindLabel = Label(entry.Kind);
		Tone = ToneOf(entry.Kind);
		Timestamp = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
		TimestampUtc = entry.TimestampUtc;
		Message = entry.Message;
		Thread = entry.ThreadId is { } thread ? $"thread {thread}" : string.Empty;
		HasThread = entry.ThreadId is not null;
		ExceptionType = entry.ExceptionType ?? string.Empty;
		HasException = ExceptionType.Length > 0;
		Frames = entry.Frames ?? [];
		Variables = entry.Variables ?? [];
		HasDetail = Frames.Count > 0 || Variables.Count > 0;
		DetailHeader = DescribeDetail(Frames.Count, Variables.Count);
	}

	/// <summary>The host's own sequence, which orders the tail and identifies a row.</summary>
	public long Sequence { get; }

	public LiveDebugEventKind Kind { get; }

	/// <summary>The kind as a pill reads it: short, and spaced rather than camel-cased.</summary>
	public string KindLabel { get; }

	/// <summary>Which of the four colours the pill takes.</summary>
	public EventTone Tone { get; }

	/// <summary>Local time to the millisecond, because two events a frame apart are the interesting case.</summary>
	public string Timestamp { get; }

	public DateTime TimestampUtc { get; }

	public string Message { get; }

	public string Thread { get; }

	public bool HasThread { get; }

	public string ExceptionType { get; }

	public bool HasException { get; }

	public IReadOnlyList<string> Frames { get; }

	public IReadOnlyList<LiveVariable> Variables { get; }

	/// <summary>Whether there is anything behind the expander, so one is only offered where there is.</summary>
	public bool HasDetail { get; }

	public string DetailHeader { get; }

	/// <summary>
	/// How much attention a kind deserves. Four rather than a severity number, because these map to
	/// the window's own palette and a reader scanning a tail is looking for the red ones.
	/// </summary>
	public enum EventTone
	{
		/// <summary>Ordinary traffic: modules, threads, log messages.</summary>
		Neutral,

		/// <summary>Something the debugger did on purpose: a stop, a step, a session note.</summary>
		Notable,

		/// <summary>A first-chance exception, which may be perfectly normal and may not.</summary>
		Caution,

		/// <summary>An unhandled exception or the process exiting.</summary>
		Critical,
	}

	public static EventTone ToneOf(LiveDebugEventKind kind) => kind switch
	{
		LiveDebugEventKind.ExceptionUnhandled or LiveDebugEventKind.ProcessExited => EventTone.Critical,
		LiveDebugEventKind.ExceptionFirstChance => EventTone.Caution,
		LiveDebugEventKind.BreakpointHit
			or LiveDebugEventKind.StepComplete
			or LiveDebugEventKind.Paused
			or LiveDebugEventKind.SessionNotice =>
			EventTone.Notable,
		_ => EventTone.Neutral,
	};

	/// <summary>
	/// <c>ExceptionFirstChance</c> as <c>exception</c>, because a pill in a column of pills has room
	/// for the distinction and not for the ceremony. The kinds that differ only in severity keep the
	/// word that separates them.
	/// </summary>
	public static string Label(LiveDebugEventKind kind) => kind switch
	{
		LiveDebugEventKind.SessionNotice => "session",
		LiveDebugEventKind.ProcessCreated => "started",
		LiveDebugEventKind.ProcessExited => "exited",
		LiveDebugEventKind.ModuleLoaded => "module",
		LiveDebugEventKind.ThreadCreated => "thread +",
		LiveDebugEventKind.ThreadExited => "thread -",
		LiveDebugEventKind.ExceptionFirstChance => "exception",
		LiveDebugEventKind.ExceptionUnhandled => "unhandled",
		LiveDebugEventKind.LogMessage => "log",
		LiveDebugEventKind.BreakpointHit => "breakpoint",
		LiveDebugEventKind.StepComplete => "step",
		_ => kind.ToString().ToLowerInvariant(),
	};

	/// <summary>What the expander offers, so a reader knows whether it is worth opening.</summary>
	public static string DescribeDetail(int frames, int variables)
	{
		if (frames == 0 && variables == 0) return string.Empty;
		if (variables == 0) return Format.Count(frames, "frame");
		if (frames == 0) return Format.Count(variables, "variable");

		return $"{Format.Count(frames, "frame")}, {Format.Count(variables, "variable")}";
	}
}
