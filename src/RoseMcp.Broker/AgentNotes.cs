using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The notes a person sends agent sessions from a window: each queued for its client until it rides on one answer
/// to that client's next call, with what the agent answered through <c>rose_reply</c>.
/// <para>
/// A note is addressed by the client table's row and held under the key the table files that row under -- the
/// session a relay named, or the transport's session -- so a session whose row goes and is made again by its next
/// call is still the session the note was sent to, and a call reaches its own notes by the same key the table
/// files it under. The one unnamed row stands for every client without a relay or a session of its own, so a note
/// to it could reach any of them and is refused. The key never leaves the broker: a window names a note's client by
/// the row id and by the hashed key the activity record carries.
/// </para>
/// <para>
/// Delivery is a claim: <see cref="Claim"/> marks every waiting note for the calling client delivered with that
/// call and hands it over, under the store's gate, so two calls arriving together never both carry one note.
/// <see cref="Release"/> puts back what a claim took when the answer could not carry it. A note whose client the
/// table no longer lists is <see cref="NoteDelivery.ClientGone"/> rather than dropped: still delivered if that
/// session calls again, and kept to be read for <see cref="GoneKeep"/> after anything last happened to it.
/// </para>
/// <para>
/// In memory only, bounded by <see cref="Capacity"/>, <see cref="MaxWaitingPerClient"/> and the text limits, since
/// every note is made at a window's say-so. One gate, held only to read and change the notes: the client table is
/// read before it is taken, so it never nests inside the table's gate, and nothing that waits happens under it.
/// </para>
/// </summary>
public sealed class AgentNotes
{
	/// <summary>The most notes held at once. Past it, the oldest delivered note goes first, then the oldest whose client has gone.</summary>
	public const int Capacity = 256;

	/// <summary>The most notes one client may have waiting for its next call.</summary>
	public const int MaxWaitingPerClient = 16;

	/// <summary>The longest a note's text or an answer may be, in characters.</summary>
	public const int MaxText = 4000;

	/// <summary>The longest a subject's readable line is kept, in characters; a longer one is cut.</summary>
	public const int MaxReadable = 300;

	/// <summary>The most answers one note keeps.</summary>
	public const int MaxReplies = 8;

	/// <summary>How long a note whose client has gone is kept after the last thing that happened to it.</summary>
	public static readonly TimeSpan GoneKeep = TimeSpan.FromDays(1);

	private const int MaxReference = 1024;

	private static readonly AsyncLocal<NoteCaller?> Ambient = new();

	/// <summary>The key of the one row every client with no relay and no session of its own shares.</summary>
	private static readonly string Unnamed = ConnectedClients.KeyOf(relayed: null, transportSession: null);

	private readonly ConnectedClients _clients;
	private readonly TimeProvider _time;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();
	private readonly List<Note> _notes = [];
	private long _claims;

	/// <summary>Reads who is connected from <paramref name="clients"/>, and the clock from <paramref name="options"/>.</summary>
	public AgentNotes(ConnectedClients clients, IOptions<BrokerOptions> options, ILogger<AgentNotes>? logger = null)
	{
		_clients = clients;
		_time = options.Value.TimeProvider;
		_logger = logger ?? NullLogger<AgentNotes>.Instance;
	}

	/// <summary>Pulsed when a note is sent, delivered, put back or answered, which is what a window's wait on the notes is woken by.</summary>
	public ChangeSignal Changed { get; } = new();

	/// <summary>
	/// Raised once an agent has answered a note, with the note as it stands, its newest answer last: how an answer
	/// reaches a person while no window is open. Raised outside the gate, and a handler that throws is logged and
	/// fails nothing.
	/// </summary>
	public event Action<AgentNote>? Replied;

	/// <summary>
	/// The client the call in flight came from, for <c>rose_reply</c>: set by the call filter that delivers notes, for
	/// the length of the call, as the relay's name for its session and the transport's session.
	/// </summary>
	internal static NoteCaller? Caller => Ambient.Value;

	private DateTimeOffset Now => _time.GetUtcNow();

	/// <summary>Sets <see cref="Caller"/> for the current call, restoring what was there when disposed.</summary>
	internal static IDisposable UseCaller(string? relayed, string? transportSession) => new CallerScope(new NoteCaller(relayed, transportSession));

	/// <summary>Queues a note for the client <paramref name="request"/> names, to ride on that client's next answer.</summary>
	/// <exception cref="KeyNotFoundException">The table lists no client by that id.</exception>
	/// <exception cref="ArgumentException">
	/// The note cannot be taken as it is: no text, too long, a subject its kind does not allow, the unnamed client, or
	/// no room for it.
	/// </exception>
	public AgentNote Post(NoteRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		var clientId = request.Client?.Trim();
		if (string.IsNullOrEmpty(clientId))
		{
			throw new ArgumentException("A note names the client it is for, by the id GET /operator/clients lists it under.");
		}

		var text = Text(request.Text, "A note");
		var subject = Validated(request.Subject);

		var present = _clients.ByKey();
		var (key, row) = present.FirstOrDefault(pair => string.Equals(pair.Value.Id, clientId, StringComparison.Ordinal));
		if (key is null || row is null)
		{
			throw new KeyNotFoundException(
				$"The broker lists no client {clientId}. GET /operator/clients lists the ones there are; a session's row goes "
					+ "a couple of minutes after its relay lets go of the broker.");
		}

		if (key == Unnamed)
		{
			throw new ArgumentException(
				"That row stands for every client that reached the broker over http with neither the relay nor a session of "
					+ "its own, so a note to it would reach whichever of them called next. Register Rose with the agent through "
					+ "the relay to send it notes.");
		}

		var now = Now;
		AgentNote posted;

		lock (_gate)
		{
			Expire(present, now);

			var waiting = _notes.Count(note => note.Key == key && note.DeliveredUtc is null);
			if (waiting >= MaxWaitingPerClient)
			{
				throw new ArgumentException(
					$"{MaxWaitingPerClient} notes are already waiting for that session's next call, the most one session holds. "
						+ "They are delivered together with its next answer, of any tool.");
			}

			if (!MakeRoom(present))
			{
				throw new ArgumentException(
					$"The broker already holds {Capacity} notes, every one waiting for a session that is still connected, so "
						+ "none can make room for this one until those sessions call.");
			}

			var note = new Note(MintId(), key, row, subject, text, now);
			_notes.Add(note);
			posted = note.Describe(present);
		}

		Changed.Pulse();

		return posted;
	}

	/// <summary>
	/// Every note the broker holds, the earliest sent first, or only those for the client whose row is
	/// <paramref name="client"/>, with the version a long poll waits past.
	/// </summary>
	public NoteList Describe(string? client = null)
	{
		var present = _clients.ByKey();
		var now = Now;
		List<AgentNote> notes;

		lock (_gate)
		{
			Expire(present, now);

			notes =
			[
				.. _notes
					.Select(note => note.Describe(present))
					.Where(note => string.IsNullOrEmpty(client) || string.Equals(note.ClientId, client, StringComparison.Ordinal)),
			];
		}

		return new NoteList { Notes = notes, Version = ViewVersion.Of(notes) };
	}

	/// <summary>
	/// Hands every note waiting for the client a call came from to that call, marking each delivered with it, so no
	/// other call is given the same note. The caller adds them to the call's answer, or gives them back with
	/// <see cref="Release"/> where it cannot.
	/// </summary>
	/// <param name="relayed">The client a relay named, as <see cref="CallSession.Of"/> gives it with no transport session; null for none.</param>
	/// <param name="transportSession">The transport's own session, where it has one.</param>
	/// <param name="tool">The tool whose answer carries them.</param>
	/// <param name="callId">The activity record's id for the call, where it was recorded.</param>
	public NoteClaim Claim(string? relayed, string? transportSession, string? tool, string? callId)
	{
		var key = KeyOf(relayed, transportSession);
		var now = Now;
		List<AgentNote> claimed = [];
		long token;

		lock (_gate)
		{
			token = ++_claims;

			foreach (var note in _notes.Where(note => note.Key == key && note.DeliveredUtc is null))
			{
				note.Deliver(now, ConnectedClients.Clean(tool, 128), callId, token);
				claimed.Add(note.Describe(currentRow: null));
			}
		}

		if (claimed.Count > 0) Changed.Pulse();

		return new NoteClaim(claimed, token);
	}

	/// <summary>
	/// Puts back what <paramref name="claim"/> took, waiting again for the client's next call: for an answer that
	/// could not carry it. A note an answer has confirmed since is left delivered.
	/// </summary>
	public void Release(NoteClaim claim)
	{
		ArgumentNullException.ThrowIfNull(claim);
		if (claim.Notes.Count == 0) return;

		lock (_gate)
		{
			foreach (var note in _notes.Where(note => note.ClaimToken == claim.Token && note.Replies.Count == 0)) note.Undeliver();
		}

		Changed.Pulse();
	}

	/// <summary>
	/// Adds an agent's answer to a note sent to the client the call came from, marking the note delivered if nothing
	/// had yet: an answer is proof it arrived.
	/// </summary>
	/// <exception cref="InvalidOperationException">The call names no session, or the note holds as many answers as one keeps.</exception>
	/// <exception cref="ArgumentException">No note by that id was sent to this client, or the answer is empty or too long.</exception>
	public NoteReplied Reply(string? relayed, string? transportSession, string? noteId, string? text, string? callId)
	{
		var key = KeyOf(relayed, transportSession);
		if (key == Unnamed)
		{
			throw new InvalidOperationException(
				"This call names no session -- it came without the relay, over a transport that gives none -- so Rose cannot "
					+ "tell which session is answering, and notes go only to sessions it can tell apart.");
		}

		var id = noteId?.Trim() ?? string.Empty;
		var answer = Text(text, "An answer");
		var now = Now;
		AgentNote answered;
		NoteSubject subject;
		int replies;

		lock (_gate)
		{
			var note = _notes.FirstOrDefault(candidate => candidate.Key == key && string.Equals(candidate.Id, id, StringComparison.Ordinal))
				?? throw new ArgumentException(
					$"No note '{id}' was sent to this session. A note's id is in the notice that delivered it, and the broker "
						+ "forgets every note when it restarts.");

			if (note.Replies.Count >= MaxReplies)
			{
				throw new InvalidOperationException(
					$"That note already holds {MaxReplies} answers, the most one keeps. Say the rest in the conversation.");
			}

			note.Answer(new NoteReply { Text = answer, RepliedUtc = now, ActivityEntry = callId }, ToolNames.Reply);
			answered = note.Describe(currentRow: null);
			subject = note.Subject;
			replies = note.Replies.Count;
		}

		Changed.Pulse();
		RaiseReplied(answered);

		return new NoteReplied { NoteId = id, About = subject.Readable, RepliedUtc = now, Replies = replies };
	}

	/// <summary>
	/// What an agent is told of <paramref name="note"/> in the notices of the answer that delivers it: that a person
	/// wrote it, what about -- in words and by the references the agent can look up -- what it says, and how to answer.
	/// </summary>
	public static string Notice(AgentNote note)
	{
		var sent = note.PostedUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

		return $"A note from the person at the RoseMCP window, sent {sent} UTC, about {About(note.Subject)}: \"{note.Text}\" "
			+ $"Answer it with rose_reply (noteId \"{note.Id}\") and they read the answer under the note, in the window they are watching.";
	}

	/// <summary>What a note is about, in words and then by its references, as the agent is told it.</summary>
	private static string About(NoteSubject subject)
	{
		List<string> references = subject.Kind switch
		{
			NoteSubjectKind.DebugValue =>
			[
				$"debug session {subject.Session}",
				.. Optional("thread", subject.ThreadId),
				.. Optional("frame", subject.FrameIndex),
				$"value path {subject.Path}",
			],
			NoteSubjectKind.Thread => [$"debug session {subject.Session}", $"thread {subject.ThreadId}"],
			NoteSubjectKind.Frame =>
			[
				$"debug session {subject.Session}",
				.. Optional("thread", subject.ThreadId),
				$"frame {subject.FrameIndex}",
			],
			NoteSubjectKind.Element => [$"debug session {subject.Session}", $"element {subject.Element}"],
			NoteSubjectKind.WorkspaceReason =>
			[
				$"workspace {subject.Workspace}",
				$"reason kind {subject.ReasonKind}",
				.. subject.Reason is { } reason ? [$"reason {reason}"] : Array.Empty<string>(),
			],
			_ => [$"your call recorded as activity entry {subject.ActivityEntry}"],
		};

		return $"{subject.Readable} ({string.Join(", ", references)})";
	}

	private static IEnumerable<string> Optional(string name, int? value) => value is { } number ? [$"{name} {number}"] : [];

	/// <summary>
	/// The subject as the store keeps it: its readable line cleaned to one line, and only the references its kind
	/// carries, each checked for the shape the tool that takes it would accept.
	/// </summary>
	/// <exception cref="ArgumentException">Something its kind needs is missing or malformed.</exception>
	private static NoteSubject Validated(NoteSubject? subject)
	{
		if (subject is null) throw new ArgumentException("A note says what it is about: its subject is missing.");

		if (!Enum.IsDefined(subject.Kind))
		{
			throw new ArgumentException(
				$"'{subject.Kind}' is not a kind of subject; a note is about one of {string.Join(", ", Enum.GetNames<NoteSubjectKind>())}.");
		}

		var readable = Cleaned(subject.Readable, MaxReadable, keepLines: false)
			?? throw new ArgumentException("A note's subject says in words what it is about: its readable line is empty.");

		return subject.Kind switch
		{
			NoteSubjectKind.DebugValue => new NoteSubject
			{
				Kind = subject.Kind,
				Readable = readable,
				Session = SessionId(subject.Session),
				ThreadId = NonNegative(subject.ThreadId, "threadId", required: false),
				FrameIndex = NonNegative(subject.FrameIndex, "frameIndex", required: false),
				Path = ValuePathOf(subject.Path),
			},
			NoteSubjectKind.Thread => new NoteSubject
			{
				Kind = subject.Kind,
				Readable = readable,
				Session = SessionId(subject.Session),
				ThreadId = NonNegative(subject.ThreadId, "threadId", required: true),
			},
			NoteSubjectKind.Frame => new NoteSubject
			{
				Kind = subject.Kind,
				Readable = readable,
				Session = SessionId(subject.Session),
				ThreadId = NonNegative(subject.ThreadId, "threadId", required: false),
				FrameIndex = NonNegative(subject.FrameIndex, "frameIndex", required: true),
			},
			NoteSubjectKind.Element => new NoteSubject
			{
				Kind = subject.Kind,
				Readable = readable,
				Session = SessionId(subject.Session),
				Element = ElementHandle(subject.Element),
			},
			NoteSubjectKind.WorkspaceReason => new NoteSubject
			{
				Kind = subject.Kind,
				Readable = readable,
				Workspace = WorkspacePath(subject.Workspace),
				ReasonKind = ReasonKindOf(subject.ReasonKind),
				Reason = Cleaned(subject.Reason, MaxReadable, keepLines: false),
			},
			_ => new NoteSubject
			{
				Kind = subject.Kind,
				Readable = readable,
				ActivityEntry = ActivityEntryId(subject.ActivityEntry),
			},
		};
	}

	private static string SessionId(string? session)
	{
		var wellFormed = session is { Length: > 0 and <= 64 } && session.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

		return wellFormed
			? session!
			: throw new ArgumentException("A note about a debugged app names its debug session, by the id GET /operator/sessions lists it under.");
	}

	private static int? NonNegative(int? value, string name, bool required)
	{
		if (value is null && required) throw new ArgumentException($"A note about that kind of subject gives its {name}.");
		if (value is < 0) throw new ArgumentException($"A subject's {name} counts from zero; {value} is not one.");

		return value;
	}

	private static string ValuePathOf(string? path)
	{
		var text = Cleaned(path, MaxReference, keepLines: false);

		try
		{
			ValuePath.Parse(text);
		}
		catch (ArgumentException exception)
		{
			throw new ArgumentException($"A note about a value gives the value's path, as rose_debug_evaluate takes one. {exception.Message}");
		}

		return text!;
	}

	private static string ElementHandle(string? element)
	{
		var handle = element?.Trim();

		return handle is { Length: > 0 } && ulong.TryParse(handle, NumberStyles.None, CultureInfo.InvariantCulture, out _)
			? handle
			: throw new ArgumentException("A note about an element gives its handle in the live visual tree: a whole number, as the tree lists it.");
	}

	private static string WorkspacePath(string? workspace)
	{
		var path = Cleaned(workspace, MaxReference, keepLines: false);

		return path is not null && Path.IsPathFullyQualified(path)
			? path
			: throw new ArgumentException("A note about a workspace's reason names the solution by the absolute path a workspace row gives.");
	}

	private static string ReasonKindOf(string? kind)
	{
		var wellFormed = kind is { Length: > 0 and <= 64 } && kind.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

		return wellFormed
			? kind!
			: throw new ArgumentException("A note about a workspace's reason gives the reason's kind, as a reason in health names it: project-not-loaded.");
	}

	private static string ActivityEntryId(string? entry)
	{
		var wellFormed = entry is { Length: 16 } && entry.All(char.IsAsciiHexDigitLower);

		return wellFormed
			? entry!
			: throw new ArgumentException("A note about a call gives the activity record's id for it: sixteen lowercase hex digits.");
	}

	/// <summary>A note's text or an answer, cleaned and checked: present, and no longer than <see cref="MaxText"/>.</summary>
	private static string Text(string? text, string what)
	{
		if (text is { Length: > MaxText })
		{
			throw new ArgumentException($"{what} is at most {MaxText:N0} characters, and this one is {text.Length:N0}.");
		}

		return Cleaned(text, MaxText, keepLines: true) ?? throw new ArgumentException($"{what} says something, and this one is empty.");
	}

	/// <summary>
	/// <paramref name="value"/> without control or formatting characters -- an escape sequence, a right-to-left
	/// override -- or a surrogate standing alone, with its line breaks kept as <c>\n</c> where
	/// <paramref name="keepLines"/> says and made spaces otherwise, trimmed, and cut to <paramref name="max"/>
	/// characters without splitting a pair; null where nothing is left. What a person or an agent writes here is
	/// shown to the other and written into a result.
	/// </summary>
	internal static string? Cleaned(string? value, int max, bool keepLines)
	{
		if (value is null) return null;

		var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
		var kept = new StringBuilder(Math.Min(text.Length, max));

		for (var index = 0; index < text.Length && kept.Length < max; index++)
		{
			var character = text[index];

			if (char.IsHighSurrogate(character))
			{
				var paired = index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
				if (!paired) continue;
				if (kept.Length + 2 > max) break;

				kept.Append(character).Append(text[++index]);
				continue;
			}

			if (character is '\n' or '\t')
			{
				kept.Append(keepLines ? character : ' ');
				continue;
			}

			var unprintable = char.IsControl(character)
				|| char.IsLowSurrogate(character)
				|| char.GetUnicodeCategory(character) == UnicodeCategory.Format;
			if (!unprintable) kept.Append(character);
		}

		var cleaned = kept.ToString().Trim();

		return cleaned.Length == 0 ? null : cleaned;
	}

	private static string KeyOf(string? relayed, string? transportSession) =>
		ConnectedClients.KeyOf(relayed, string.IsNullOrWhiteSpace(transportSession) ? null : transportSession);

	private static string MintId() => "note-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

	/// <summary>Drops every note whose client has gone and that nothing has happened to for <see cref="GoneKeep"/>. Under the gate.</summary>
	private void Expire(IReadOnlyDictionary<string, ClientSummary> present, DateTimeOffset now) =>
		_notes.RemoveAll(note => !present.ContainsKey(note.Key) && now - note.ChangedUtc > GoneKeep);

	/// <summary>
	/// Whether there is room for one more note, made where the store is full by dropping the oldest delivered note,
	/// else the oldest whose client has gone; a note still waiting for a connected client is never dropped. Under the gate.
	/// </summary>
	private bool MakeRoom(IReadOnlyDictionary<string, ClientSummary> present)
	{
		if (_notes.Count < Capacity) return true;

		var oldest = _notes.FirstOrDefault(note => note.DeliveredUtc is not null)
			?? _notes.FirstOrDefault(note => !present.ContainsKey(note.Key));

		return oldest is not null && _notes.Remove(oldest);
	}

	private void RaiseReplied(AgentNote note)
	{
		if (Replied is not { } replied) return;

		foreach (var handler in replied.GetInvocationList().Cast<Action<AgentNote>>())
		{
			try
			{
				handler(note);
			}
			catch (Exception exception)
			{
				_logger.LogWarning(exception, "Something told of an answer to note {Note} failed; the answer is kept.", note.Id);
			}
		}
	}

	/// <summary>What <see cref="Claim"/> handed to a call: the notes, as they are now delivered, to add to its answer.</summary>
	public sealed class NoteClaim
	{
		internal NoteClaim(IReadOnlyList<AgentNote> notes, long token)
		{
			Notes = notes;
			Token = token;
		}

		/// <summary>The notes claimed, the earliest sent first; empty where none was waiting.</summary>
		public IReadOnlyList<AgentNote> Notes { get; }

		internal long Token { get; }
	}

	/// <summary>The client a call came from, as <see cref="Caller"/> holds it.</summary>
	internal sealed record NoteCaller(string? Relayed, string? TransportSession);

	private sealed class CallerScope : IDisposable
	{
		private readonly NoteCaller? _previous;

		public CallerScope(NoteCaller caller)
		{
			_previous = Ambient.Value;
			Ambient.Value = caller;
		}

		public void Dispose() => Ambient.Value = _previous;
	}

	/// <summary>One note. Read and written only under the store's gate.</summary>
	private sealed class Note(string id, string key, ClientSummary row, NoteSubject subject, string text, DateTimeOffset postedUtc)
	{
		public string Id { get; } = id;

		/// <summary>The key the client table files the note's client under; never shown.</summary>
		public string Key { get; } = key;

		public NoteSubject Subject { get; } = subject;

		public List<NoteReply> Replies { get; } = [];

		public DateTimeOffset PostedUtc { get; } = postedUtc;

		/// <summary>When the note was sent, delivered or last answered: what <see cref="GoneKeep"/> counts from.</summary>
		public DateTimeOffset ChangedUtc { get; private set; } = postedUtc;

		public DateTimeOffset? DeliveredUtc { get; private set; }

		/// <summary>The claim that delivered it, so a claim gives back only what it took.</summary>
		public long? ClaimToken { get; private set; }

		private string? DeliveredTool { get; set; }

		private string? DeliveredWith { get; set; }

		public void Deliver(DateTimeOffset now, string? tool, string? callId, long? token)
		{
			DeliveredUtc = now;
			DeliveredTool = tool;
			DeliveredWith = callId;
			ClaimToken = token;
			ChangedUtc = now;
		}

		public void Undeliver()
		{
			DeliveredUtc = null;
			DeliveredTool = null;
			DeliveredWith = null;
			ClaimToken = null;
		}

		public void Answer(NoteReply reply, string tool)
		{
			Replies.Add(reply);
			if (DeliveredUtc is null) Deliver(reply.RepliedUtc, tool, reply.ActivityEntry, token: null);

			ChangedUtc = reply.RepliedUtc;
		}

		/// <summary>The note as a window reads it, its client named by the row the table keeps for it now where there is one.</summary>
		public AgentNote Describe(IReadOnlyDictionary<string, ClientSummary> present) =>
			Describe(present.TryGetValue(Key, out var current) ? current : null, present.ContainsKey(Key));

		/// <summary>The note as a call that just reached it reads it: its client is the one calling, so it is not gone.</summary>
		public AgentNote Describe(ClientSummary? currentRow) => Describe(currentRow, clientPresent: true);

		private AgentNote Describe(ClientSummary? currentRow, bool clientPresent) => new()
		{
			Id = Id,
			ClientId = currentRow?.Id ?? row.Id,
			ClientKey = CallRecording.Hashed(Key),
			ClientLabel = row.Label,
			ClientFolder = row.WorkingDirectory ?? row.Roots.FirstOrDefault(),
			ClientName = row.ClientName,
			Subject = Subject,
			Text = text,
			PostedUtc = PostedUtc,
			Delivery = DeliveredUtc is not null ? NoteDelivery.Delivered : clientPresent ? NoteDelivery.Waiting : NoteDelivery.ClientGone,
			DeliveredUtc = DeliveredUtc,
			DeliveredTool = DeliveredTool,
			DeliveredWith = DeliveredWith,
			Replies = [.. Replies],
		};
	}
}
