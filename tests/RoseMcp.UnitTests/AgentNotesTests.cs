using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The notes a person sends agent sessions: each waits for its own session, rides on exactly one answer, outlives
/// its session's row long enough to be read, and is answered only by the session it was sent to. A note that went
/// twice, went to the wrong session, or vanished when a row expired is a person's words lost or misdirected.
/// </summary>
public sealed class AgentNotesTests
{
	private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

	private static readonly string Shop = Path.Combine(Path.GetTempPath(), "rose-notes", "Shop.slnx");

	[Test]
	public void A_note_waits_for_its_session_and_rides_on_one_answer_of_that_session_alone()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var theirs = Session(clients);

		var posted = notes.Post(Note(IdOf(clients, mine), "Why did this fail?"));

		posted.Id.ShouldStartWith("note-", Case.Sensitive);
		posted.Delivery.ShouldBe(NoteDelivery.Waiting);
		posted.ClientKey.ShouldBe(CallRecording.KeyOf(mine, transportSession: null), "a window finds the session's calls by the key the record carries");
		posted.DeliveredUtc.ShouldBeNull();

		notes.Claim(theirs, null, ToolNames.Outline, "0000000000000001").Notes.ShouldBeEmpty("another session's call carries none of it");

		var claimed = notes.Claim(mine, null, ToolNames.Outline, "0000000000000002").Notes.ShouldHaveSingleItem();
		claimed.Id.ShouldBe(posted.Id);
		claimed.Delivery.ShouldBe(NoteDelivery.Delivered);

		notes.Claim(mine, null, ToolNames.Diagnostics, "0000000000000003").Notes.ShouldBeEmpty("a note rides on one answer");

		var described = notes.Describe().Notes.ShouldHaveSingleItem();
		described.Delivery.ShouldBe(NoteDelivery.Delivered);
		described.DeliveredUtc.ShouldBe(Start);
		described.DeliveredTool.ShouldBe(ToolNames.Outline);
		described.DeliveredWith.ShouldBe("0000000000000002");
	}

	/// <summary>Calls answered together each claim under the store's gate, so between them every note goes once.</summary>
	[Test]
	public async Task Calls_answered_together_never_carry_one_note_twice()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var id = IdOf(clients, mine);

		for (var index = 0; index < AgentNotes.MaxWaitingPerClient; index++) notes.Post(Note(id, $"note {index}"));

		var claims = await Task.WhenAll(Enumerable.Range(0, 64).Select(call => Task.Run(() =>
			notes.Claim(mine, null, ToolNames.Outline, $"{call:x16}").Notes)));

		var carried = claims.SelectMany(claim => claim).Select(note => note.Id).ToList();
		carried.Count.ShouldBe(AgentNotes.MaxWaitingPerClient);
		carried.Distinct(StringComparer.Ordinal).Count().ShouldBe(AgentNotes.MaxWaitingPerClient);
		notes.Describe().Notes.ShouldAllBe(note => note.Delivery == NoteDelivery.Delivered);
	}

	/// <summary>The unnamed row is every client without a relay or a session of its own; a note to it would reach any of them.</summary>
	[Test]
	public void A_note_to_the_unnamed_client_is_refused_saying_why()
	{
		var (notes, clients, _) = Store();
		clients.Called(relayed: null, transportSession: null, "direct-client", "0.1");
		var unnamed = clients.Describe().ShouldHaveSingleItem();

		var refusal = Should.Throw<ArgumentException>(() => notes.Post(Note(unnamed.Id, "Hello")));

		refusal.Message.ShouldContain("relay");
		notes.Describe().Notes.ShouldBeEmpty();
	}

	[Test]
	public void A_note_to_a_client_the_table_does_not_list_is_not_found()
	{
		var (notes, _, _) = Store();

		Should.Throw<KeyNotFoundException>(() => notes.Post(Note("client-00000000", "Hello"))).Message.ShouldContain("client-00000000");
	}

	/// <summary>
	/// A session whose row expires before its next call leaves its note readable as gone, still delivered if the same
	/// session calls again -- under the row its call makes -- and forgotten a day after anything last happened to it.
	/// </summary>
	[Test]
	public void A_note_whose_session_goes_stays_readable_and_reaches_the_session_if_it_returns()
	{
		var (notes, clients, clock) = Store();
		var mine = Session(clients);
		var first = IdOf(clients, mine);
		notes.Post(Note(first, "Still there?"));

		clock.Advance(ConnectedClients.IdleLimit + TimeSpan.FromMinutes(1));

		var gone = notes.Describe().Notes.ShouldHaveSingleItem();
		gone.Delivery.ShouldBe(NoteDelivery.ClientGone);
		gone.ClientId.ShouldBe(first, "a gone session is named by the row the note was sent to");

		clients.Called(mine, transportSession: null, clientName: null, clientVersion: null);
		var second = IdOf(clients, mine);
		second.ShouldNotBe(first);

		var back = notes.Describe().Notes.ShouldHaveSingleItem();
		back.Delivery.ShouldBe(NoteDelivery.Waiting);
		back.ClientId.ShouldBe(second, "the session's row now");
		notes.Claim(mine, null, ToolNames.Outline, null).Notes.ShouldHaveSingleItem();
	}

	[Test]
	public void A_note_whose_session_has_gone_is_forgotten_a_day_after_it_was_seen_gone()
	{
		var (notes, clients, clock) = Store();
		var mine = Session(clients);
		notes.Post(Note(IdOf(clients, mine), "Gone soon"));

		clock.Advance(ConnectedClients.IdleLimit + TimeSpan.FromMinutes(1));
		notes.Describe().Notes.ShouldHaveSingleItem().Delivery.ShouldBe(NoteDelivery.ClientGone);

		clock.Advance(AgentNotes.GoneKeep - TimeSpan.FromMinutes(1));
		notes.Describe().Notes.ShouldHaveSingleItem("a day from when its session was seen gone has not passed");

		clock.Advance(TimeSpan.FromMinutes(2));
		notes.Describe().Notes.ShouldBeEmpty();
	}

	/// <summary>
	/// A note delivered and answered long before its session ends -- the session working on for more than a day --
	/// is still shown once the session's row goes, for a day from then, rather than vanishing with the row.
	/// </summary>
	[Test]
	public void A_note_answered_long_before_its_session_goes_is_shown_for_a_day_after()
	{
		var (notes, clients, clock) = Store();
		var mine = Session(clients);
		var note = notes.Post(Note(IdOf(clients, mine), "Worth a look?"));
		notes.Claim(mine, null, ToolNames.Outline, null);
		notes.Reply(mine, null, note.Id, "Looked.", null);

		for (var hour = TimeSpan.Zero; hour < AgentNotes.GoneKeep + TimeSpan.FromHours(1); hour += TimeSpan.FromHours(1))
		{
			clock.Advance(TimeSpan.FromHours(1));
			clients.Called(mine, transportSession: null, clientName: null, clientVersion: null);
			notes.Describe().Notes.ShouldHaveSingleItem("its session is still working");
		}

		clock.Advance(ConnectedClients.IdleLimit + TimeSpan.FromMinutes(1));
		var shown = notes.Describe().Notes.ShouldHaveSingleItem("its row has just gone");
		shown.Delivery.ShouldBe(NoteDelivery.Delivered);
		shown.Replies.ShouldHaveSingleItem();

		clock.Advance(AgentNotes.GoneKeep - TimeSpan.FromMinutes(1));
		notes.Describe().Notes.ShouldHaveSingleItem();

		clock.Advance(TimeSpan.FromMinutes(2));
		notes.Describe().Notes.ShouldBeEmpty();
	}

	/// <summary>
	/// A note waiting for a quiet session its relay keeps watching for more than a day waits on, then reads as gone for
	/// a day once the relay lets go and the row goes.
	/// </summary>
	[Test]
	public void A_note_waiting_for_a_quiet_session_outlives_a_day_and_is_shown_gone_for_a_day_after()
	{
		var (notes, clients, relays, clock) = WatchedStore();
		var hex = CallSession.Mint();
		var relay = RelayProtocol.NewRelayId();
		var watching = relays.Watch(relay, processId: 4242);
		clients.Introduce(relay, new RelayClient { Session = hex });
		var mine = CallSession.Relayed(hex)!;

		notes.Post(Note(IdOf(clients, mine), "When you are back"));

		clock.Advance(AgentNotes.GoneKeep + TimeSpan.FromHours(6));
		notes.Describe().Notes.ShouldHaveSingleItem().Delivery.ShouldBe(NoteDelivery.Waiting, "its relay still watches");

		watching.Dispose();
		clock.Advance(ConnectedClients.GoneGrace + TimeSpan.FromMinutes(1));
		notes.Describe().Notes.ShouldHaveSingleItem().Delivery.ShouldBe(NoteDelivery.ClientGone);

		clock.Advance(AgentNotes.GoneKeep - TimeSpan.FromMinutes(1));
		notes.Describe().Notes.ShouldHaveSingleItem();

		clock.Advance(TimeSpan.FromMinutes(2));
		notes.Describe().Notes.ShouldBeEmpty();
	}

	private static (AgentNotes Notes, ConnectedClients Clients, RelayConnections Relays, SteerableClock Clock) WatchedStore()
	{
		var clock = new SteerableClock(Start);
		var options = Options.Create(new BrokerOptions { TimeProvider = clock });
		var relays = new RelayConnections(clock);
		var clients = new ConnectedClients(relays, options);

		return (new AgentNotes(clients, options), clients, relays, clock);
	}

	/// <summary>
	/// Only the session a note was sent to can answer it; anyone else is told no such note was sent to them, so a
	/// session cannot learn of another's notes by guessing ids; and a call naming no session answers nothing.
	/// </summary>
	[Test]
	public void Only_the_session_a_note_was_sent_to_can_answer_it()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var theirs = Session(clients);
		var note = notes.Post(Note(IdOf(clients, mine), "Is the width right?"));
		notes.Claim(mine, null, ToolNames.DebugEvaluate, "00000000000000aa");

		Should.Throw<ArgumentException>(() => notes.Reply(theirs, null, note.Id, "Not mine", null)).Message.ShouldContain("No note");
		Should.Throw<InvalidOperationException>(() => notes.Reply(null, null, note.Id, "Who am I", null));

		AgentNote? told = null;
		notes.Replied += replied => told = replied;

		var replied = notes.Reply(mine, null, note.Id, "It is 2.5, as set.", "00000000000000bb");

		replied.NoteId.ShouldBe(note.Id);
		replied.About.ShouldBe(note.Subject.Readable);
		replied.Replies.ShouldBe(1);

		var described = notes.Describe().Notes.ShouldHaveSingleItem();
		var answer = described.Replies.ShouldHaveSingleItem();
		answer.Text.ShouldBe("It is 2.5, as set.");
		answer.ActivityEntry.ShouldBe("00000000000000bb");
		told.ShouldNotBeNull().Replies.ShouldHaveSingleItem().Text.ShouldBe("It is 2.5, as set.");
	}

	/// <summary>An answer is proof a note arrived, so one to a note still waiting marks it delivered with the answer.</summary>
	[Test]
	public void An_answer_to_a_waiting_note_delivers_it()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var note = notes.Post(Note(IdOf(clients, mine), "Seen this?"));

		notes.Reply(mine, null, note.Id, "Yes.", "00000000000000cc");

		var described = notes.Describe().Notes.ShouldHaveSingleItem();
		described.Delivery.ShouldBe(NoteDelivery.Delivered);
		described.DeliveredTool.ShouldBe(ToolNames.Reply);
		described.DeliveredWith.ShouldBe("00000000000000cc");
		notes.Claim(mine, null, ToolNames.Outline, null).Notes.ShouldBeEmpty("it is delivered, so no answer carries it again");
	}

	/// <summary>Whatever is told of an answer, a handler that throws fails neither the answer nor the next handler.</summary>
	[Test]
	public void A_handler_that_throws_fails_nothing()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var note = notes.Post(Note(IdOf(clients, mine), "Hello"));
		var heard = false;

		notes.Replied += _ => throw new InvalidOperationException("the tray has gone");
		notes.Replied += _ => heard = true;

		notes.Reply(mine, null, note.Id, "Hello back.", null).Replies.ShouldBe(1);
		heard.ShouldBeTrue();
	}

	[Test]
	public void A_note_keeps_at_most_so_many_answers()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var note = notes.Post(Note(IdOf(clients, mine), "Talk to me"));

		for (var index = 0; index < AgentNotes.MaxReplies; index++) notes.Reply(mine, null, note.Id, $"answer {index}", null);

		Should.Throw<InvalidOperationException>(() => notes.Reply(mine, null, note.Id, "one more", null));
		notes.Describe().Notes.ShouldHaveSingleItem().Replies.Count.ShouldBe(AgentNotes.MaxReplies);
	}

	/// <summary>A claim whose answer could not carry its notes gives them back, and the next answer carries them.</summary>
	[Test]
	public void A_released_claim_waits_for_the_next_answer()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		notes.Post(Note(IdOf(clients, mine), "Again"));

		var claim = notes.Claim(mine, null, ToolNames.Outline, null);
		notes.Release(claim);

		notes.Describe().Notes.ShouldHaveSingleItem().Delivery.ShouldBe(NoteDelivery.Waiting);
		notes.Claim(mine, null, ToolNames.Outline, null).Notes.ShouldHaveSingleItem();
	}

	[Test]
	public void A_session_holds_at_most_so_many_waiting_notes()
	{
		var (notes, clients, _) = Store();
		var id = IdOf(clients, Session(clients));

		for (var index = 0; index < AgentNotes.MaxWaitingPerClient; index++) notes.Post(Note(id, $"note {index}"));

		Should.Throw<ArgumentException>(() => notes.Post(Note(id, "one too many")));
	}

	/// <summary>A full store makes room from what is settled -- the oldest delivered first -- and never from a note still waiting for a connected session.</summary>
	[Test]
	public void A_full_store_makes_room_from_delivered_notes_and_refuses_rather_than_drop_a_waiting_one()
	{
		var (notes, clients, _) = Store();
		var sessions = Enumerable.Range(0, AgentNotes.Capacity / AgentNotes.MaxWaitingPerClient).Select(_ => Session(clients)).ToList();

		foreach (var session in sessions)
		{
			for (var index = 0; index < AgentNotes.MaxWaitingPerClient; index++) notes.Post(Note(IdOf(clients, session), $"note {index}"));
		}

		var extra = Session(clients);
		Should.Throw<ArgumentException>(() => notes.Post(Note(IdOf(clients, extra), "no room")));

		var oldest = notes.Describe().Notes[0].Id;
		notes.Claim(sessions[0], null, ToolNames.Outline, null);

		notes.Post(Note(IdOf(clients, extra), "room now"));

		var held = notes.Describe().Notes;
		held.Count.ShouldBe(AgentNotes.Capacity);
		held.ShouldNotContain(note => note.Id == oldest, "the oldest delivered note made room");
		held.Count(note => note.Delivery == NoteDelivery.Waiting).ShouldBe(AgentNotes.Capacity - AgentNotes.MaxWaitingPerClient + 1);
	}

	/// <summary>A subject keeps only the references its kind carries, and is refused without one its kind needs.</summary>
	[Test]
	public void A_subject_keeps_what_its_kind_carries_and_is_refused_without_what_it_needs()
	{
		var (notes, clients, _) = Store();
		var id = IdOf(clients, Session(clients));

		var value = notes.Post(new NoteRequest
		{
			Client = id,
			Text = "Is this right?",
			Subject = new NoteSubject
			{
				Kind = NoteSubjectKind.DebugValue,
				Readable = "_pen.Width = 2.5",
				Session = "a1b2c3d4",
				ThreadId = 7,
				FrameIndex = 0,
				Path = "this._pen.Width",
				Workspace = Shop,
				ActivityEntry = "0123456789abcdef",
			},
		}).Subject;

		value.Path.ShouldBe("this._pen.Width");
		value.ThreadId.ShouldBe(7);
		value.Workspace.ShouldBeNull("a value is in no workspace");
		value.ActivityEntry.ShouldBeNull();

		NoteSubject[] refused =
		[
			new() { Kind = NoteSubjectKind.DebugValue, Readable = "x", Session = "a1b2c3d4" },
			new() { Kind = NoteSubjectKind.DebugValue, Readable = "x", Session = "a1b2c3d4", Path = "local:x" },
			new() { Kind = NoteSubjectKind.Thread, Readable = "Thread 7", Session = "a1b2c3d4" },
			new() { Kind = NoteSubjectKind.Frame, Readable = "Frame 2", Session = "a1b2c3d4", FrameIndex = -1 },
			new() { Kind = NoteSubjectKind.Element, Readable = "Button", Session = "a1b2c3d4", Element = "OkButton" },
			new() { Kind = NoteSubjectKind.Element, Readable = "Button", Session = "not a session", Element = "42" },
			new() { Kind = NoteSubjectKind.WorkspaceReason, Readable = "Not loaded", Workspace = "Shop.slnx", ReasonKind = ReasonKinds.ProjectNotLoaded },
			new() { Kind = NoteSubjectKind.WorkspaceReason, Readable = "Not loaded", Workspace = Shop, ReasonKind = "Project Not Loaded" },
			new() { Kind = NoteSubjectKind.ActivityCall, Readable = "rose_outline", ActivityEntry = "123" },
			new() { Kind = NoteSubjectKind.ActivityCall, Readable = "  \n ", ActivityEntry = "0123456789abcdef" },
			new() { Kind = (NoteSubjectKind)99, Readable = "?" },
		];

		foreach (var subject in refused)
		{
			Should.Throw<ArgumentException>(() => notes.Post(new NoteRequest { Client = id, Subject = subject, Text = "?" }), $"{subject}");
		}

		notes.Describe().Notes.ShouldHaveSingleItem();
	}

	/// <summary>
	/// What a person and an agent write is shown to the other and written into a result, so it loses its control and
	/// formatting characters and keeps its lines; a readable line is one line, cut; a note too long is refused rather
	/// than cut, since a cut note says something its writer did not.
	/// </summary>
	[Test]
	public void Text_loses_what_it_should_not_carry_and_keeps_its_lines()
	{
		var (notes, clients, _) = Store();
		var id = IdOf(clients, Session(clients));

		var posted = notes.Post(Note(id, "First line\r\nsecond\u001b[31m line‮") with
		{
			Subject = Call() with { Readable = "rose_outline\nWorkerActivity " + new string('x', 400) },
		});

		posted.Text.ShouldBe("First line\nsecond[31m line");
		posted.Subject.Readable.ShouldStartWith("rose_outline WorkerActivity", Case.Sensitive);
		posted.Subject.Readable.Length.ShouldBe(AgentNotes.MaxReadable);

		Should.Throw<ArgumentException>(() => notes.Post(Note(id, new string('a', AgentNotes.MaxText + 1)))).Message.ShouldContain("4,000");
		Should.Throw<ArgumentException>(() => notes.Post(Note(id, " \u0007 ")));
	}

	[Test]
	public void The_notice_says_a_person_wrote_it_what_it_is_about_and_how_to_answer()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		notes.Post(new NoteRequest
		{
			Client = IdOf(clients, mine),
			Text = "Why is this one degraded?",
			Subject = new NoteSubject
			{
				Kind = NoteSubjectKind.WorkspaceReason,
				Readable = "Project Legacy.Tests did not load",
				Workspace = Shop,
				ReasonKind = ReasonKinds.ProjectNotLoaded,
				Reason = "project-not-loaded:Legacy.Tests",
			},
		});

		var note = notes.Claim(mine, null, ToolNames.Outline, null).Notes.ShouldHaveSingleItem();
		var notice = AgentNotes.Notice(note);

		notice.ShouldContain("the person at the RoseMCP window");
		notice.ShouldContain("Project Legacy.Tests did not load");
		notice.ShouldContain($"workspace {Shop}");
		notice.ShouldContain("reason project-not-loaded:Legacy.Tests");
		notice.ShouldContain("\"Why is this one degraded?\"");
		notice.ShouldContain($"rose_reply (noteId \"{note.Id}\")");
	}

	/// <summary>A window's wait on the notes is woken by every change, and its version moves with what it holds.</summary>
	[Test]
	public void Every_change_wakes_a_window_and_moves_the_version()
	{
		var (notes, clients, _) = Store();
		var mine = Session(clients);
		var empty = notes.Describe().Version;

		var beforePost = notes.Changed.Next;
		var note = notes.Post(Note(IdOf(clients, mine), "Hello"));
		beforePost.IsCompleted.ShouldBeTrue();
		var posted = notes.Describe().Version;
		posted.ShouldNotBe(empty);

		var idle = notes.Changed.Next;
		notes.Claim(Session(clients), null, ToolNames.Outline, null);
		idle.IsCompleted.ShouldBeFalse("a call that carried nothing changed nothing");

		notes.Claim(mine, null, ToolNames.Outline, null);
		idle.IsCompleted.ShouldBeTrue();
		var delivered = notes.Describe().Version;
		delivered.ShouldNotBe(posted);

		var beforeReply = notes.Changed.Next;
		notes.Reply(mine, null, note.Id, "Hi.", null);
		beforeReply.IsCompleted.ShouldBeTrue();
		notes.Describe().Version.ShouldNotBe(delivered);
	}

	private static NoteRequest Note(string client, string text) => new() { Client = client, Subject = Call(), Text = text };

	private static NoteSubject Call() => new()
	{
		Kind = NoteSubjectKind.ActivityCall,
		Readable = "rose_outline WorkerActivity at 14:05:31",
		ActivityEntry = "0123456789abcdef",
	};

	/// <summary>A relayed session that has called once, so the table keeps a row for it.</summary>
	private static string Session(ConnectedClients clients)
	{
		var relayed = CallSession.Relayed(CallSession.Mint())!;
		clients.Called(relayed, transportSession: null, clientName: null, clientVersion: null);

		return relayed;
	}

	private static string IdOf(ConnectedClients clients, string relayed) =>
		clients.Of(relayed, transportSession: null).ShouldNotBeNull().Id;

	private static (AgentNotes Notes, ConnectedClients Clients, SteerableClock Clock) Store()
	{
		var (notes, clients, _, clock) = WatchedStore();

		return (notes, clients, clock);
	}

	private sealed class SteerableClock(DateTimeOffset start) : TimeProvider
	{
		private DateTimeOffset _now = start;

		public void Advance(TimeSpan by) => _now += by;

		public override DateTimeOffset GetUtcNow() => _now;
	}
}
