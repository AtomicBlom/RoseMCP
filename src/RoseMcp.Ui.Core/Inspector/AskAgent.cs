using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// What "Ask an agent" in the Inspector writes about and whom it can go to: each row a person can select made into a
/// subject the agent can look up -- the session, thread, frame, value path, event or breakpoint -- with a line saying
/// it in words, and the agent sessions in the debug session offered as the note's recipients.
/// <para>
/// One place for every kind, so a local, a watch and a frame are described one way however they were reached; the
/// broker keeps only the references a kind carries, and refuses a subject missing one it needs.
/// </para>
/// </summary>
public static class AskAgent
{
	/// <summary>The shortcut that asks about the row last selected, as a tooltip and a menu item name it.</summary>
	public const string Shortcut = "Ctrl+Shift+A";

	/// <summary>Why nothing can be asked where the session is the person's alone.</summary>
	public const string NotShared =
		"You started this session and have not shared it with an agent session, so there is nobody in it to ask. Share it first.";

	/// <summary>Why nothing can be asked where the agent session in it is not connected.</summary>
	public const string AgentGone =
		"The agent session in this debug session is not connected through the relay, so a note cannot reach it.";

	/// <summary>A thread, by its id and what it is sitting in.</summary>
	public static NoteSubject Thread(string sessionId, ThreadRow thread) => new()
	{
		Kind = NoteSubjectKind.Thread,
		Readable = $"thread {thread.Id}, in {thread.TopFrame}",
		Session = sessionId,
		ThreadId = thread.Id,
	};

	/// <summary>A frame of a thread's stack, by its index from the top and the thread it is on.</summary>
	public static NoteSubject Frame(string sessionId, int? threadId, FrameRow frame) => new()
	{
		Kind = NoteSubjectKind.Frame,
		Readable = frame.HasWhere ? $"frame {frame.Index}, {frame.Method} at {frame.Where}" : $"frame {frame.Index}, {frame.Method}",
		Session = sessionId,
		ThreadId = threadId,
		FrameIndex = frame.Index,
	};

	/// <summary>
	/// A value read in a frame -- a local, an argument, a field under one, or a watch -- by the path that reads it again
	/// and the frame and thread it was read in, said as the person saw it.
	/// </summary>
	public static NoteSubject Value(string sessionId, int? threadId, FrameRow? frame, string name, string value, string typeName, string path)
	{
		var shown = typeName.Length > 0 ? $"{name} = {value} ({typeName})" : $"{name} = {value}";
		var where = frame is null ? string.Empty : $" in {frame.Method}{(frame.Line is { } line ? $" at line {line}" : string.Empty)}";

		return new NoteSubject
		{
			Kind = NoteSubjectKind.DebugValue,
			Readable = $"{shown}{where}",
			Session = sessionId,
			ThreadId = threadId,
			FrameIndex = frame?.Index ?? 0,
			Path = path,
		};
	}

	/// <summary>A value in the locals tree.</summary>
	public static NoteSubject Value(string sessionId, int? threadId, FrameRow? frame, VariableNode node) =>
		Value(sessionId, threadId, frame, node.Name, node.Value, node.TypeName, node.Path);

	/// <summary>A breakpoint, by its id and where it is.</summary>
	public static NoteSubject Breakpoint(string sessionId, BreakpointRow breakpoint) => new()
	{
		Kind = NoteSubjectKind.Breakpoint,
		Readable = $"breakpoint {breakpoint.Id} at {breakpoint.Location}",
		Session = sessionId,
		Breakpoint = breakpoint.Id,
	};

	/// <summary>A tracepoint, which a note names the way it names a breakpoint.</summary>
	public static NoteSubject Tracepoint(string sessionId, TracepointRow tracepoint) => new()
	{
		Kind = NoteSubjectKind.Breakpoint,
		Readable = $"tracepoint {tracepoint.Id} at {tracepoint.Location}",
		Session = sessionId,
		Breakpoint = tracepoint.Id,
	};

	/// <summary>
	/// An element of the live visual tree, by its handle, said as the tree names it and where its markup declares it --
	/// with what live edits changed on it this session, newest first, since that is usually what the question is about.
	/// </summary>
	public static NoteSubject Element(string sessionId, XamlNodeRow row, IReadOnlyList<LiveXamlChange> changes)
	{
		ArgumentNullException.ThrowIfNull(row);
		ArgumentNullException.ThrowIfNull(changes);

		var named = row.Address is { Length: > 0 } address && address != row.Label ? $"{row.Label} ({address})" : row.Label;
		var where = row.HasWhere ? $" in {row.Where}" : string.Empty;
		var latest = changes.MaxBy(change => change.Sequence);
		var changed = latest is null ? string.Empty : $", changed: {XamlChangeText.Line(latest)}";

		return new NoteSubject
		{
			Kind = NoteSubjectKind.Element,
			Readable = $"element {named}{where}{changed}",
			Session = sessionId,
			Element = row.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture),
		};
	}

	/// <summary>
	/// How old a press of the in-app panel's Ask button can be when a window first reads it and still open the note.
	/// The broker opens the window within a second or two of the press, and a window takes a few more to start; a
	/// press older than this was made before the window was asked for, and opening a note about it unprompted would
	/// be a window answering a question nobody is waiting on.
	/// </summary>
	public static readonly TimeSpan PanelAskFreshFor = TimeSpan.FromSeconds(60);

	/// <summary>
	/// The press of the in-app panel's Ask button a window should open its note about, or null: one other than the
	/// press it last handled, and pressed recently enough that somebody is waiting on it.
	/// </summary>
	/// <param name="summary">The session as the window just read it.</param>
	/// <param name="handled">The press the window last opened a note about; null for none.</param>
	public static LiveXamlAsk? PanelAskToOpen(LiveAppSessionSummary summary, LiveXamlAsk? handled)
	{
		ArgumentNullException.ThrowIfNull(summary);

		if (summary.XamlAsk is not { } ask || ask.IsSamePress(handled)) return null;

		return summary.XamlAskAge is { } age && age <= PanelAskFreshFor ? ask : null;
	}

	/// <summary>
	/// What a press of the in-app panel's Ask button was about, as the note's subject. A pick or a measurement is about
	/// an element -- a measurement's anchor, with the element it was taken to in its words -- and a colour is about the
	/// app's window, since a pixel belongs to no one element.
	/// </summary>
	public static NoteSubject FromPanel(string sessionId, LiveXamlAsk ask)
	{
		ArgumentNullException.ThrowIfNull(ask);

		var readable = ask.Kind switch
		{
			"measurement" when ask.OtherElement is { } other => $"{ask.Readable} (to element {Handle(other)})",
			_ => ask.Readable,
		};

		return ask.Element is { } element && ask.Kind != "colour"
			? new NoteSubject
			{
				Kind = NoteSubjectKind.Element,
				Readable = readable,
				Session = sessionId,
				Element = Handle(element),
			}
			: new NoteSubject
			{
				Kind = NoteSubjectKind.AppWindow,
				Readable = readable,
				Session = sessionId,
			};
	}

	private static string Handle(ulong handle) => handle.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// Whom a note about something in a debug session can go to: the agent sessions in it -- the one that started it,
	/// or the one a person shared theirs with -- that the client table lists as sessions it can tell apart, where
	/// <paramref name="persons"/> says whether the person started it. Says why there is none where there is none.
	/// </summary>
	public static (IReadOnlyList<NoteRecipient> Recipients, string Unreachable) Recipients(
		IReadOnlyList<LiveAppAgent> agents,
		bool persons,
		IReadOnlyList<ClientSummary> clients)
	{
		var named = clients.Where(client => client.Named).ToList();
		var recipients = agents
			.Select(agent => named.FirstOrDefault(client => client.Id == agent.ClientId))
			.OfType<ClientSummary>()
			.DistinctBy(client => client.Id)
			.Select(client => NoteTargets.Recipient(client, named))
			.ToList();

		if (recipients.Count > 0) return (recipients, string.Empty);

		return (recipients, persons && agents.Count == 0 ? NotShared : AgentGone);
	}
}
