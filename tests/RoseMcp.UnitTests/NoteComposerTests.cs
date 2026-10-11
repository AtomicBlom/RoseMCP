using System.Net;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// The window's half of a note: the composer on a call and on a workspace's reason, whom each can reach, and how a
/// note and its answers read once sent. A composer that offered the wrong session, lost what a person wrote to a
/// refusal, or said a note had arrived when it only waits for a call, would send the person's words astray.
/// </summary>
public sealed class NoteComposerTests
{
	private static readonly DateTimeOffset Noon = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

	private static readonly string Shop = Path.Combine(Path.GetTempPath(), "rose-notes", "Shop.slnx");

	[Test]
	public void The_composer_holds_a_person_to_what_the_broker_takes() =>
		NoteComposer.MaxText.ShouldBe(AgentNotes.MaxText);

	/// <summary>Nothing goes without something to say, a subject, and a session to say it to; and it says which.</summary>
	[Test]
	public void A_note_needs_words_a_subject_and_a_session_to_go_to()
	{
		var composer = new NoteComposer();
		composer.Text = "Why?";
		composer.CanSend.ShouldBeFalse("it is about nothing yet");

		composer.Regard(Subject());
		composer.Offer([], NoteTargets.CallerGone);
		composer.CanSend.ShouldBeFalse("there is nobody to send it to");
		composer.CanReach.ShouldBeFalse();
		composer.Destination.ShouldBe(NoteTargets.CallerGone);

		composer.Offer([new NoteRecipient("client-1", "ink width")], NoteTargets.CallerGone);
		composer.CanSend.ShouldBeTrue();
		composer.Destination.ShouldBe("To ink width. It arrives with that session's next Rose call.");

		composer.Text = "   ";
		composer.CanSend.ShouldBeFalse("blank is nothing to say");

		composer.Text = new string('a', NoteComposer.MaxText + 1);
		composer.CanSend.ShouldBeFalse("longer than the broker takes");
	}

	/// <summary>Taken, the note's text is cleared and the composer says it waits for a call; the request names the session, the subject and the words.</summary>
	[Test]
	public async Task A_note_the_broker_takes_clears_the_composer_and_says_it_waits_for_a_call()
	{
		var composer = Ready();
		composer.Toggle();
		composer.Text = "  Why did this fail?  ";
		NoteRequest? sent = null;

		var note = await composer.SendAsync(
			(request, _) =>
			{
				sent = request;
				return Task.FromResult(Note(request));
			},
			CancellationToken.None);

		note.ShouldNotBeNull();
		sent.ShouldNotBeNull();
		sent.Client.ShouldBe("client-1");
		sent.Text.ShouldBe("Why did this fail?");
		sent.Subject.ShouldBe(Subject());

		composer.Text.ShouldBeEmpty();
		composer.IsOpen.ShouldBeFalse();
		composer.Status.ShouldContain("ink width");
		composer.Status.ShouldContain("next Rose call");
		composer.HasProblem.ShouldBeFalse();
	}

	/// <summary>Refused, the composer stays open with every word the person wrote, and says why in the broker's words.</summary>
	[Test]
	public async Task A_note_the_broker_refuses_keeps_what_was_written_and_says_why()
	{
		var composer = Ready();
		composer.Toggle();
		composer.Text = "Still there?";

		var note = await composer.SendAsync(
			(_, _) => throw new OperatorException(OperatorFailure.NotFound, "The broker lists no client client-1.", HttpStatusCode.NotFound),
			CancellationToken.None);

		note.ShouldBeNull();
		composer.IsOpen.ShouldBeTrue();
		composer.Text.ShouldBe("Still there?");
		composer.Problem.ShouldBe("The broker lists no client client-1.");
		composer.IsSending.ShouldBeFalse();
	}

	/// <summary>A refresh that offers the same sessions in another order keeps the one the person chose.</summary>
	[Test]
	public void A_refresh_keeps_the_session_the_person_chose()
	{
		var composer = Ready();
		composer.Offer([new NoteRecipient("client-1", "ink width"), new NoteRecipient("client-2", "activity-log")], NoteTargets.NobodyToAsk);
		composer.HasChoice.ShouldBeTrue();

		composer.RecipientIndex = 1;
		composer.Offer([new NoteRecipient("client-2", "activity-log"), new NoteRecipient("client-1", "ink width")], NoteTargets.NobodyToAsk);

		composer.Recipient.ShouldNotBeNull().Id.ShouldBe("client-2");
		composer.RecipientIndex.ShouldBe(0);
	}

	/// <summary>
	/// A call's note goes to the session that made it: by its row, else -- across a restart, which gives the session a
	/// new row -- by the key its calls carry; never to a client without the relay, and nowhere once the session has gone.
	/// </summary>
	[Test]
	public void A_calls_note_goes_to_the_session_that_made_it()
	{
		var before = OperationRowTests.Call("before", Noon.AddMinutes(-30), Noon.AddMinutes(-29)) with { ClientId = "row-1", ClientKey = "key-a" };
		var after = OperationRowTests.Call("after", Noon.AddMinutes(-5), Noon.AddMinutes(-4)) with { ClientId = "row-2", ClientKey = "key-a" };
		var unnamedCall = OperationRowTests.Call("unnamed", Noon.AddMinutes(-3), Noon.AddMinutes(-2)) with { ClientId = "row-u", ClientKey = "key-u" };
		var goneCall = OperationRowTests.Call("gone", Noon.AddMinutes(-60), Noon.AddMinutes(-59)) with { ClientId = "row-9", ClientKey = "key-z" };
		ClientSummary[] clients = [AgentCardTests.Client("row-2", label: "ink width"), AgentCardTests.Client("row-u", named: false)];
		ActivityEntry[] entries = [before, after, unnamedCall, goneCall];

		NoteTargets.CallerOf("row-1", "key-a", clients, entries).ShouldHaveSingleItem().ShouldBe(new NoteRecipient("row-2", "ink width"));
		NoteTargets.CallerOf("row-2", "key-a", clients, entries).ShouldHaveSingleItem().Id.ShouldBe("row-2");

		var rows = entries.Select(entry => new OperationRow(entry, Noon)).ToList();
		NoteTargets.Address(rows, clients, entries);

		rows.Single(row => row.Id == "before").Composer.Recipient.ShouldNotBeNull().Id.ShouldBe("row-2");
		rows.Single(row => row.Id == "unnamed").Composer.Destination.ShouldBe(NoteTargets.UnnamedCaller);
		rows.Single(row => row.Id == "gone").Composer.Destination.ShouldBe(NoteTargets.CallerGone);
	}

	/// <summary>A reason's note can go to any session the broker tells apart, those that used the workspace first, the newest caller first.</summary>
	[Test]
	public void A_reasons_note_offers_the_workspaces_users_first()
	{
		var other = Path.Combine(Path.GetTempPath(), "rose-notes", "Other.slnx");
		ClientSummary[] clients =
		[
			AgentCardTests.Client("row-1", label: "early") with { LastCallUtc = Noon },
			AgentCardTests.Client("row-2", label: "late"),
			AgentCardTests.Client("row-3", label: "elsewhere"),
			AgentCardTests.Client("row-u", named: false),
		];
		ActivityEntry[] entries =
		[
			OperationRowTests.Call("a", Noon.AddMinutes(-10), Noon.AddMinutes(-9)) with { ClientId = "row-1", ClientKey = "k1", Workspace = Shop },
			OperationRowTests.Call("b", Noon.AddMinutes(-2), Noon.AddMinutes(-1)) with { ClientId = "row-2", ClientKey = "k2", Workspace = Shop },
			OperationRowTests.Call("c", Noon.AddMinutes(-1), Noon) with { ClientId = "row-3", ClientKey = "k3", Workspace = other },
			OperationRowTests.Call("d", Noon.AddMinutes(-1), Noon) with { ClientId = "row-u", ClientKey = "ku", Workspace = Shop },
		];

		NoteTargets.For(Shop, clients, entries).Select(recipient => recipient.Name).ShouldBe(["late", "early", "elsewhere"]);
		NoteTargets.For(Shop, [], entries).ShouldBeEmpty();
	}

	/// <summary>Two sessions sharing a name are told apart by their rows, or the person cannot choose between them.</summary>
	[Test]
	public void Sessions_sharing_a_name_are_told_apart()
	{
		ClientSummary[] clients = [AgentCardTests.Client("row-1", label: "ink width"), AgentCardTests.Client("row-2", label: "ink width")];

		NoteTargets.For(Shop, clients, []).Select(recipient => recipient.Name).ShouldBe(["ink width (row-1)", "ink width (row-2)"], ignoreOrder: true);
	}

	/// <summary>A call's subject names its entry in the record and says the call in words, how it ended included.</summary>
	[Test]
	public void A_calls_subject_names_its_entry_and_says_how_it_ended()
	{
		var failed = OperationRowTests.Call("0123456789abcdef", Noon, Noon.AddSeconds(1)) with { Outcome = ActivityOutcome.Failed };

		var subject = new OperationRow(failed, Noon).Composer.Subject.ShouldNotBeNull();

		subject.Kind.ShouldBe(NoteSubjectKind.ActivityCall);
		subject.ActivityEntry.ShouldBe("0123456789abcdef");
		subject.Readable.ShouldBe("rose_find_references ActivityLog.Begin, which failed");
	}

	/// <summary>A reason's subject names the solution, the kind and the id rose_workspace_acknowledge takes, and says Rose's line.</summary>
	[Test]
	public void A_reasons_subject_names_its_workspace_kind_and_id()
	{
		var reason = new ReasonRow(new HealthReason
		{
			Id = $"{ReasonKinds.ProjectNotLoaded}:Legacy.Tests",
			Kind = ReasonKinds.ProjectNotLoaded,
			Effect = HealthEffect.MayAffectSomeProjects,
			Projects = ["Legacy.Tests"],
			Line = "Project Legacy.Tests did not load successfully.",
		});

		reason.OfferNote(Shop, [new NoteRecipient("row-1", "ink width")]);

		var subject = reason.Composer.Subject.ShouldNotBeNull();
		subject.Kind.ShouldBe(NoteSubjectKind.WorkspaceReason);
		subject.Workspace.ShouldBe(Shop);
		subject.ReasonKind.ShouldBe(ReasonKinds.ProjectNotLoaded);
		subject.Reason.ShouldBe("project-not-loaded:Legacy.Tests");
		subject.Readable.ShouldBe("Project Legacy.Tests did not load successfully.");
		reason.Composer.Recipient.ShouldNotBeNull().Id.ShouldBe("row-1");
	}

	/// <summary>
	/// A card offers its calls' composers its own session, or nobody where nothing names it; it shows the notes sent
	/// to it; and the notes to sessions no card stands for are kept for the view to show rather than lost.
	/// </summary>
	[Test]
	public void A_card_addresses_its_calls_and_shows_its_notes()
	{
		var call = OperationRowTests.Call("c1", Noon.AddMinutes(-1), Noon) with { ClientId = "row-1" };

		var card = new AgentCard(AgentCardTests.Client("row-1", label: "ink width"), [call], AgentContext.Empty, Noon);
		card.Operations.ShouldHaveSingleItem().Composer.Recipient.ShouldBe(new NoteRecipient("row-1", "ink width"));

		var unnamed = new AgentCard(AgentCardTests.Client("row-u", named: false), [call with { ClientId = "row-u" }], AgentContext.Empty, Noon);
		unnamed.Operations.ShouldHaveSingleItem().Composer.Destination.ShouldBe(NoteTargets.UnnamedCaller);

		var mine = Note("note-1", "row-1", NoteDelivery.Delivered) with { DeliveredUtc = Noon, DeliveredTool = ToolNames.Outline };
		var theirs = Note("note-2", "row-2", NoteDelivery.Waiting);
		var gone = Note("note-3", "row-9", NoteDelivery.ClientGone);

		card.ShowNotes([mine, theirs, gone]);

		card.Notes.ShouldHaveSingleItem().Id.ShouldBe("note-1");
		card.NotesSummary.ShouldContain("1 note delivered");
		AgentCard.GoneNotes([mine, theirs, gone], [AgentCardTests.Client("row-1"), AgentCardTests.Client("row-2")])
			.ShouldHaveSingleItem().Id.ShouldBe("note-3");
	}

	/// <summary>A note's row says where its delivery has got to, with which call, and shows the agent's answers under it.</summary>
	[Test]
	public void A_notes_row_says_where_it_has_got_to_and_shows_the_answers()
	{
		var waiting = new NoteRow(Note("note-1", "row-1", NoteDelivery.Waiting));
		waiting.IsWaiting.ShouldBeTrue();
		waiting.State.ShouldBe("waiting for the session's next Rose call");

		var gone = new NoteRow(Note("note-2", "row-1", NoteDelivery.ClientGone));
		gone.IsGone.ShouldBeTrue();
		gone.State.ShouldContain("the session went before its next call");

		var answered = Note("note-3", "row-1", NoteDelivery.Delivered) with
		{
			DeliveredUtc = Noon,
			DeliveredTool = ToolNames.Outline,
			DeliveredWith = "00000000000000aa",
			Replies = [new NoteReply { Text = "It is 2.5.", RepliedUtc = Noon.AddMinutes(1), ActivityEntry = "00000000000000bb" }],
		};

		var row = new NoteRow(answered);
		row.IsDelivered.ShouldBeTrue();
		row.State.ShouldStartWith("delivered ", Case.Sensitive);
		row.State.ShouldEndWith($"with {ToolNames.Outline}", Case.Sensitive);
		row.DeliveredWith.ShouldContain("00000000000000aa");
		row.Replies.ShouldHaveSingleItem().Text.ShouldBe("It is 2.5.");
		NoteRow.Summarise([answered]).ShouldContain("it answered one");
	}

	/// <summary>A debug subject's references are what the agent was given to look it up by, and are shown under the note.</summary>
	[Test]
	public void A_debug_subjects_references_are_shown()
	{
		var value = new NoteSubject
		{
			Kind = NoteSubjectKind.DebugValue,
			Readable = "_pen.Width = 2.5",
			Session = "a1b2c3d4",
			ThreadId = 7,
			FrameIndex = 0,
			Path = "this._pen.Width",
		};

		NoteRow.ReferencesOf(value).ShouldBe("session a1b2c3d4  ·  thread 7  ·  frame 0  ·  this._pen.Width");
		NoteRow.ReferencesOf(Subject()).ShouldBeEmpty("a call's entry id says nothing to a person");
		NoteRow.ReferencesOf(new NoteSubject { Kind = NoteSubjectKind.Element, Readable = "OK", Session = "s", Element = "42" })
			.ShouldBe("session s  ·  element 42");
	}

	private static NoteComposer Ready()
	{
		var composer = new NoteComposer();
		composer.Regard(Subject());
		composer.Offer([new NoteRecipient("client-1", "ink width")], NoteTargets.CallerGone);

		return composer;
	}

	private static NoteSubject Subject() => new()
	{
		Kind = NoteSubjectKind.ActivityCall,
		Readable = "rose_outline WorkerActivity",
		ActivityEntry = "0123456789abcdef",
	};

	private static AgentNote Note(NoteRequest request) => new()
	{
		Id = "note-0123456789abcdef",
		ClientId = request.Client,
		ClientKey = "0123456789abcdef",
		Subject = request.Subject,
		Text = request.Text,
		PostedUtc = Noon,
		Delivery = NoteDelivery.Waiting,
	};

	private static AgentNote Note(string id, string client, NoteDelivery delivery) => new()
	{
		Id = id,
		ClientId = client,
		ClientKey = "0123456789abcdef",
		ClientLabel = "ink width",
		Subject = Subject(),
		Text = "Why?",
		PostedUtc = Noon,
		Delivery = delivery,
	};
}
