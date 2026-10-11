using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// A note being written to an agent about one thing the person is looking at: what it is about, which session it
/// goes to, what the person writes, and how sending it went. The composer on an Activity row and on a workspace's
/// reason binds to one of these; the view that owns the row says what it is about and whom it can go to.
/// <para>
/// Nothing here pushes: a note rides on the session's next answer, so the composer says so rather than promising it
/// arrives now. What the person was writing survives a refresh of the row it is on, since the row is updated in
/// place and this with it, and is cleared only once the broker has taken the note.
/// </para>
/// </summary>
public sealed class NoteComposer : Observable
{
	/// <summary>The longest note the broker takes, which the text box holds the person to.</summary>
	public const int MaxText = 4000;

	private NoteSubject? _subject;
	private string _text = string.Empty;
	private bool _isOpen;
	private bool _isSending;
	private string _problem = string.Empty;
	private string _status = string.Empty;
	private string _unreachable = string.Empty;
	private int _recipientIndex = -1;

	/// <summary>What the note is about, or null until the row has said.</summary>
	public NoteSubject? Subject => _subject;

	/// <summary>What the note is about, in the words the agent is told first.</summary>
	public string About => _subject?.Readable ?? string.Empty;

	/// <summary>The sessions it can go to, the likeliest first.</summary>
	public ObservableCollection<NoteRecipient> Recipients { get; } = [];

	/// <summary>Which of <see cref="Recipients"/> it goes to. Bound both ways.</summary>
	public int RecipientIndex
	{
		get => _recipientIndex;
		set
		{
			var index = value >= 0 && value < Recipients.Count ? value : Recipients.Count > 0 ? 0 : -1;
			Set(ref _recipientIndex, index);
			Changed();
		}
	}

	/// <summary>The session it goes to, or null where there is none to choose.</summary>
	public NoteRecipient? Recipient => RecipientIndex >= 0 && RecipientIndex < Recipients.Count ? Recipients[RecipientIndex] : null;

	/// <summary>Whether there is more than one session to choose from, which is when the choice is shown.</summary>
	public bool HasChoice => Recipients.Count > 1;

	/// <summary>Whether there is a session it can go to at all.</summary>
	public bool CanReach => Recipient is not null;

	/// <summary>Where it goes and when it arrives, or why it cannot go anywhere.</summary>
	public string Destination => Recipient is { } recipient
		? $"To {recipient.Name}. It arrives with that session's next Rose call."
		: _unreachable;

	/// <summary>What the person writes. Bound both ways.</summary>
	public string Text
	{
		get => _text;
		set
		{
			Set(ref _text, value ?? string.Empty);
			Changed();
		}
	}

	/// <summary>Whether the composer is open for writing.</summary>
	public bool IsOpen
	{
		get => _isOpen;
		set
		{
			Set(ref _isOpen, value);
			OnPropertyChanged(nameof(IsClosed));
		}
	}

	public bool IsClosed => !IsOpen;

	/// <summary>Whether a note is on its way to the broker.</summary>
	public bool IsSending
	{
		get => _isSending;
		private set
		{
			Set(ref _isSending, value);
			Changed();
		}
	}

	/// <summary>Why the last send failed, in the broker's words where it gave some.</summary>
	public string Problem
	{
		get => _problem;
		private set
		{
			Set(ref _problem, value);
			OnPropertyChanged(nameof(HasProblem));
		}
	}

	public bool HasProblem => Problem.Length > 0;

	/// <summary>What became of the last note sent from here: that it was sent, when, and that it waits for a call.</summary>
	public string Status
	{
		get => _status;
		private set
		{
			Set(ref _status, value);
			OnPropertyChanged(nameof(HasStatus));
		}
	}

	public bool HasStatus => Status.Length > 0;

	/// <summary>Whether what is written can be sent: something to say, no longer than the broker takes, somewhere to send it, and nothing on its way.</summary>
	public bool CanSend =>
		!IsSending && _subject is not null && Recipient is not null && Text.Trim().Length > 0 && Text.Length <= MaxText;

	/// <summary>Says what the note is about. The row calls this when it is made, or when it learns where it is.</summary>
	public void Regard(NoteSubject subject)
	{
		if (Equals(_subject, subject)) return;

		_subject = subject;
		OnPropertyChanged(nameof(Subject));
		OnPropertyChanged(nameof(About));
		Changed();
	}

	/// <summary>
	/// Offers the sessions it can go to, keeping the one the person chose where it is still offered, and says why it
	/// cannot go anywhere where <paramref name="recipients"/> is empty.
	/// </summary>
	public void Offer(IReadOnlyList<NoteRecipient> recipients, string unreachable)
	{
		var listChanged = !Recipients.SequenceEqual(recipients);
		var reasonChanged = !string.Equals(_unreachable, unreachable, StringComparison.Ordinal);

		// Offered on every refresh of the row it sits on, which is every call an agent makes; most change nothing.
		if (!listChanged && !reasonChanged) return;

		_unreachable = unreachable;

		if (listChanged)
		{
			var chosen = Recipient?.Id;

			Recipients.Clear();
			foreach (var recipient in recipients) Recipients.Add(recipient);

			var kept = Recipients.ToList().FindIndex(recipient => recipient.Id == chosen);

			// Forced past the equality check, so a list that changed under an unchanged index still tells a box bound to
			// it which entry is chosen.
			_recipientIndex = -2;
			RecipientIndex = kept >= 0 ? kept : 0;
			OnPropertyChanged(nameof(HasChoice));
			OnPropertyChanged(nameof(CanReach));
		}

		Changed();
	}

	/// <summary>Opens the composer, or closes it keeping what was written.</summary>
	public void Toggle()
	{
		IsOpen = !IsOpen;
		if (IsOpen) Problem = string.Empty;
	}

	/// <summary>
	/// Sends what is written through <paramref name="send"/>. Taken, the text is cleared, the composer closes and says
	/// the note waits for the session's next call; refused, it stays open with the text and says why.
	/// </summary>
	/// <returns>The note as the broker holds it, or null where nothing was sent.</returns>
	public async Task<AgentNote?> SendAsync(Func<NoteRequest, CancellationToken, Task<AgentNote>> send, CancellationToken cancellationToken)
	{
		if (!CanSend || _subject is not { } subject || Recipient is not { } recipient) return null;

		IsSending = true;
		Problem = string.Empty;

		try
		{
			var note = await send(new NoteRequest { Client = recipient.Id, Subject = subject, Text = Text.Trim() }, cancellationToken);
			var sent = note.PostedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

			Text = string.Empty;
			IsOpen = false;
			Status = $"Sent to {recipient.Name} at {sent}; it arrives with that session's next Rose call.";

			return note;
		}
		catch (OperatorException exception)
		{
			Problem = exception.Message;
			return null;
		}
		finally
		{
			IsSending = false;
		}
	}

	private void Changed()
	{
		OnPropertyChanged(nameof(Recipient));
		OnPropertyChanged(nameof(Destination));
		OnPropertyChanged(nameof(CanSend));
	}
}
