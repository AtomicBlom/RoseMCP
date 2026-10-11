using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// A note the person sent an agent, as the window shows it: what it says and what it is about, where its delivery
/// has got to -- waiting for the session's next call, delivered with which call and when, or its session gone -- and
/// what the agent answered, under it.
/// </summary>
public sealed class NoteRow : Observable
{
	private string _state = string.Empty;
	private bool _isWaiting;
	private bool _isDelivered;
	private bool _isGone;
	private string _deliveredWith = string.Empty;

	public NoteRow(AgentNote note)
	{
		Id = note.Id;
		Text = note.Text;
		About = note.Subject.Readable;
		References = ReferencesOf(note.Subject);
		Agent = AgentNames.Of(note);
		ActivityFilter = note.ClientKey;
		Sent = $"sent {Clock(note.PostedUtc)}";

		Update(note);
	}

	/// <summary>The note's id. Rows are matched on it, so they survive a refresh.</summary>
	public string Id { get; }

	/// <summary>What the person wrote.</summary>
	public string Text { get; }

	/// <summary>What it is about, in words.</summary>
	public string About { get; }

	/// <summary>What the agent was given to look it up by: the debug session, thread, frame, path, element, workspace or reason.</summary>
	public string References { get; }

	public bool HasReferences => References.Length > 0;

	/// <summary>Which session it was sent to, as the table named it then.</summary>
	public string Agent { get; }

	/// <summary>What the Activity view filters on to show that session's calls, which outlives its row.</summary>
	public string ActivityFilter { get; }

	/// <summary>When the person sent it.</summary>
	public string Sent { get; }

	/// <summary>Where its delivery has got to, in words.</summary>
	public string State
	{
		get => _state;
		private set => Set(ref _state, value);
	}

	public bool IsWaiting
	{
		get => _isWaiting;
		private set => Set(ref _isWaiting, value);
	}

	public bool IsDelivered
	{
		get => _isDelivered;
		private set => Set(ref _isDelivered, value);
	}

	/// <summary>Whether its session went before a call could carry it.</summary>
	public bool IsGone
	{
		get => _isGone;
		private set => Set(ref _isGone, value);
	}

	/// <summary>The call that carried it, by its id in the activity record, for the tooltip.</summary>
	public string DeliveredWith
	{
		get => _deliveredWith;
		private set => Set(ref _deliveredWith, value);
	}

	/// <summary>What the agent answered, oldest first.</summary>
	public ObservableCollection<NoteAnswer> Replies { get; } = [];

	public bool HasReplies => Replies.Count > 0;

	/// <summary>Brings the row in line with the note as the broker now holds it.</summary>
	public void Update(AgentNote note)
	{
		IsWaiting = note.Delivery is not (NoteDelivery.Delivered or NoteDelivery.ClientGone);
		IsDelivered = note.Delivery == NoteDelivery.Delivered;
		IsGone = note.Delivery == NoteDelivery.ClientGone;
		State = StateOf(note);
		DeliveredWith = note.DeliveredWith is { Length: > 0 } call ? $"Carried by the call recorded as {call}." : string.Empty;

		var answers = note.Replies.Select(reply => new NoteAnswer(reply)).ToList();
		if (!Replies.SequenceEqual(answers))
		{
			Replies.Clear();
			foreach (var answer in answers) Replies.Add(answer);
		}

		OnPropertyChanged(nameof(HasReplies));
	}

	/// <summary>Where a note's delivery has got to, as its row says it.</summary>
	public static string StateOf(AgentNote note) => note.Delivery switch
	{
		NoteDelivery.Delivered when note.DeliveredUtc is { } delivered => note.DeliveredTool is { Length: > 0 } tool
			? $"delivered {Clock(delivered)} with {tool}"
			: $"delivered {Clock(delivered)}",
		NoteDelivery.Delivered => "delivered",
		NoteDelivery.ClientGone => "not delivered: the session went before its next call. It arrives if that session calls again.",
		_ => "waiting for the session's next Rose call",
	};

	/// <summary>The line under a card's notes: how many, the newest delivery, and whether the agent answered.</summary>
	public static string Summarise(IReadOnlyList<AgentNote> notes)
	{
		if (notes.Count == 0) return string.Empty;

		var facts = new List<string>();
		var waiting = notes.Count(note => note.Delivery == NoteDelivery.Waiting);
		var delivered = notes.Where(note => note.DeliveredUtc is not null).ToList();
		var answered = notes.Count(note => note.Replies.Count > 0);

		if (delivered.Count > 0)
		{
			var newest = delivered.Max(note => note.DeliveredUtc!.Value);
			facts.Add($"{Format.Count(delivered.Count, "note")} delivered, the latest at {Clock(newest)}");
		}

		if (waiting > 0) facts.Add($"{Format.Count(waiting, "note")} waiting for its next call");
		if (answered > 0) facts.Add(answered == 1 ? "it answered one" : $"it answered {answered}");

		return string.Join(Format.Separator, facts);
	}

	/// <summary>A subject's references, as the agent was given them.</summary>
	public static string ReferencesOf(NoteSubject subject)
	{
		var parts = new List<string>();

		if (subject.Session is { Length: > 0 } session) parts.Add($"session {session}");
		if (subject.ThreadId is { } thread) parts.Add($"thread {thread}");
		if (subject.FrameIndex is { } frame) parts.Add($"frame {frame}");
		if (subject.Path is { Length: > 0 } path) parts.Add(path);
		if (subject.Element is { Length: > 0 } element) parts.Add($"element {element}");
		if (subject.Workspace is { Length: > 0 } workspace) parts.Add(Format.FileLine(workspace, line: null));
		if (subject.Reason is { Length: > 0 } reason) parts.Add(reason);
		else if (subject.ReasonKind is { Length: > 0 } kind) parts.Add(kind);
		if (subject.ActivityEntry is { Length: > 0 } entry) parts.Add($"call {entry}");

		return subject.Kind == NoteSubjectKind.ActivityCall && parts.Count == 1 ? string.Empty : string.Join(Format.Separator, parts);
	}

	internal static string Clock(DateTimeOffset utc) => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>One answer an agent gave a note, as the row shows it under the note.</summary>
public sealed record NoteAnswer
{
	public NoteAnswer(NoteReply reply)
	{
		Text = reply.Text;
		Time = NoteRow.Clock(reply.RepliedUtc);
		Call = reply.ActivityEntry is { Length: > 0 } call ? $"Answered with the call recorded as {call}." : "Answered.";
	}

	/// <summary>What the agent said.</summary>
	public string Text { get; }

	/// <summary>When it said it.</summary>
	public string Time { get; }

	/// <summary>The call it answered with, for the tooltip.</summary>
	public string Call { get; }
}
