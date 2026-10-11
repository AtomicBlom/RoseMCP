using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The session stream: the app's events, the agents' calls into the session and the person's notes, interleaved in
/// the order they happened and filtered to what the person chose -- each source merged once, updated in place, and
/// bounded, with what it could not keep said.
/// </summary>
public sealed class SessionStreamTests
{
	private static readonly DateTime At = new(2026, 10, 12, 14, 2, 31, DateTimeKind.Utc);

	/// <summary>
	/// Three sources arrive in their own order -- the tail, the record, the notes -- and the stream puts each row where
	/// its time puts it, so an agent's call reads beside the stop it caused.
	/// </summary>
	[Test]
	public void Rows_from_every_source_are_in_the_order_they_happened()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Events(Event(1, LiveDebugEventKind.BreakpointHit, At)));
		stream.Absorb(Calls(Call("c1", At.AddSeconds(-36)), Call("c2", At.AddSeconds(2))));
		stream.Absorb(Notes(Note("n1", At.AddSeconds(7), replied: At.AddSeconds(33))));

		stream.Rows.Select(row => row.Key).ShouldBe(["c:c1", "e:1", "c:c2", "n:n1", "n:n1:r:0"]);
		stream.Rows.Select(row => row.Kind).ShouldBe([StreamKind.Call, StreamKind.Event, StreamKind.Call, StreamKind.Note, StreamKind.Reply]);
	}

	/// <summary>A call is one row however many reads list it, and says how it ended once it has.</summary>
	[Test]
	public void A_call_is_one_row_updated_in_place_as_it_ends()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Calls(Call("c1", At, ActivityOutcome.Running)));
		var row = stream.Rows.ShouldHaveSingleItem();
		row.Detail.ShouldBe("running");

		stream.Absorb(Calls(Call("c1", At, ActivityOutcome.Failed, error: "No such value.")));

		stream.Rows.ShouldHaveSingleItem().ShouldBeSameAs(row);
		row.Detail.ShouldBe("failed: No such value.");
		row.Tone.ShouldBe(StreamTone.Critical);
		row.Text.ShouldBe("rose_debug_evaluate value");
		row.Who.ShouldBe("PDF");
	}

	/// <summary>A call to another debug session, and notes about another, are not this stream's, whatever a page carried.</summary>
	[Test]
	public void Rows_about_another_session_are_passed_over()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Calls(Call("c1", At) with { DebugSession = "s2" }));
		stream.Absorb(Notes(Note("n1", At) with { Subject = Subject("s2") }));

		stream.Rows.ShouldBeEmpty();
		stream.IsEmpty.ShouldBeTrue();
	}

	/// <summary>
	/// A note's row follows its delivery, and each answer is added once after it: the stream says the agent received
	/// it and what it said, without a row per poll.
	/// </summary>
	[Test]
	public void A_note_follows_its_delivery_and_each_answer_is_added_once()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Notes(Note("n1", At)));
		var note = stream.Rows.ShouldHaveSingleItem();
		note.Detail.ShouldContain("waiting for the session's next Rose call");
		note.Who.ShouldBe("You → PDF");
		note.CanAsk.ShouldBeFalse("a note is already to an agent");

		var answered = Note("n1", At, replied: At.AddSeconds(30));
		stream.Absorb(Notes(answered));
		stream.Absorb(Notes(answered));

		stream.Rows.Count.ShouldBe(2);
		note.Detail.ShouldContain("delivered");
		stream.Rows[1].Text.ShouldBe("No, the zoom is 0.");
		stream.Rows[1].Who.ShouldBe("PDF replied");
	}

	/// <summary>
	/// Each filter shows its kind of row, and changing it shows the past too: the filter is applied to what was
	/// collected rather than by the host as events arrive.
	/// </summary>
	[Test]
	public void A_filter_shows_its_kind_of_row_and_the_past_with_it()
	{
		var stream = new SessionStream("s1");
		stream.Absorb(Events(
			Event(1, LiveDebugEventKind.BreakpointHit, At),
			Event(2, LiveDebugEventKind.ExceptionFirstChance, At.AddSeconds(1)),
			Event(3, LiveDebugEventKind.LogMessage, At.AddSeconds(2)),
			Event(4, LiveDebugEventKind.ModuleLoaded, At.AddSeconds(3))));
		stream.Absorb(Calls(Call("c1", At.AddSeconds(4))));
		stream.Absorb(Notes(Note("n1", At.AddSeconds(5), replied: At.AddSeconds(6))));

		Keys(stream, StreamFilter.Stops).ShouldBe(["e:1"]);
		Keys(stream, StreamFilter.Exceptions).ShouldBe(["e:2"]);
		Keys(stream, StreamFilter.Log).ShouldBe(["e:3"]);
		Keys(stream, StreamFilter.Agent).ShouldBe(["c:c1", "n:n1:r:0"]);
		Keys(stream, StreamFilter.Notes).ShouldBe(["n:n1", "n:n1:r:0"]);
		Keys(stream, StreamFilter.All).Count.ShouldBe(7);

		stream.Filter = StreamFilter.Stops;
		stream.Absorb(Events(Event(5, LiveDebugEventKind.StepComplete, At.AddSeconds(9)), Event(6, LiveDebugEventKind.LogMessage, At.AddSeconds(9))));
		stream.Rows.Select(row => row.Key).ShouldBe(["e:1", "e:5"], "a row arriving under a filter is shown only if it matches");
	}

	/// <summary>
	/// The stream is bounded, and what it trims is said. A silently truncated stream reads as a complete one, which is
	/// the difference between "nothing happened before this" and "I stopped looking".
	/// </summary>
	[Test]
	public void The_stream_is_capped_and_says_what_it_trimmed()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Page(1, SessionStream.MaxRows + 5));

		stream.Rows.Count.ShouldBe(SessionStream.MaxRows);
		stream.HasNotice.ShouldBeTrue();
		stream.Notice.ShouldContain("5 older rows were trimmed", Case.Sensitive);
		stream.Rows[^1].Key.ShouldBe($"e:{SessionStream.MaxRows + 5}");
	}

	/// <summary>The cursor advances from the page, so the next read asks only for what is new.</summary>
	[Test]
	public void Absorbing_a_page_advances_the_cursor()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Page(1, 3, nextCursor: 9));

		stream.Cursor.ShouldBe(9);
		stream.Rows.Count.ShouldBe(3);
		stream.HasNotice.ShouldBeFalse();
	}

	/// <summary>
	/// The host's buffer is bounded too, so a reader that fell behind missed events nobody will ever see. The count of
	/// them is the only thing that says the stream has a hole in it.
	/// </summary>
	[Test]
	public void Events_the_host_dropped_are_counted()
	{
		var stream = new SessionStream("s1");
		stream.Absorb(Page(1, 2, nextCursor: 2, oldestAvailable: 1, totalObserved: 2));

		// The next page starts at 20: sequences 3 to 19 went out of the host's buffer.
		stream.Absorb(Page(20, 1, nextCursor: 20, oldestAvailable: 20, totalObserved: 20));

		stream.Notice.ShouldContain("17 earlier events", Case.Sensitive);
	}

	/// <summary>
	/// An event can be asked about by the sequence that finds it again, and a call by its entry in the record; the app
	/// exiting is remembered, which is what the header's "the app exited at" reads.
	/// </summary>
	[Test]
	public void Events_and_calls_can_be_asked_about_and_an_exit_is_remembered()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Events(Event(7, LiveDebugEventKind.ExceptionFirstChance, At), Event(8, LiveDebugEventKind.ProcessExited, At.AddSeconds(1))));
		stream.Absorb(Calls(Call("0123456789abcdef", At.AddSeconds(2))));

		var thrown = stream.Rows[0].Subject.ShouldNotBeNull();
		thrown.Kind.ShouldBe(NoteSubjectKind.DebugEvent);
		thrown.Session.ShouldBe("s1");
		thrown.EventSequence.ShouldBe(7);
		thrown.Readable.ShouldBe("exception: event 7");

		var call = stream.Rows[2].Subject.ShouldNotBeNull();
		call.Kind.ShouldBe(NoteSubjectKind.ActivityCall);
		call.ActivityEntry.ShouldBe("0123456789abcdef");

		stream.ExitedUtc.ShouldBe(new DateTimeOffset(At.AddSeconds(1)));
	}

	/// <summary>An event says its thread once: added where the host's message leaves it out, and not again where it names it.</summary>
	[Test]
	public void An_event_names_its_thread_once()
	{
		var stream = new SessionStream("s1");

		stream.Absorb(Events(
			Event(1, LiveDebugEventKind.LogMessage, At) with { ThreadId = 7 },
			Event(2, LiveDebugEventKind.BreakpointHit, At) with { ThreadId = 7, Message = "Breakpoint hit on thread 7" }));

		stream.Rows[0].Text.ShouldBe("event 1 (thread 7)");
		stream.Rows[1].Text.ShouldBe("Breakpoint hit on thread 7");
	}

	/// <summary>
	/// The tail never spins on a broker that is not there: a read that fails pauses before the next, so a refused
	/// connection answered at once is asked about again seconds later rather than in a tight loop.
	/// </summary>
	[Test]
	public async Task The_tail_pauses_after_a_failed_read()
	{
		var stream = new SessionStream("s1");
		var reads = 0;
		var failures = 0;

		var tail = stream.Tail(
			(_, _, _) =>
			{
				Interlocked.Increment(ref reads);
				throw new OperatorException(OperatorFailure.Unreachable, "gone", null);
			},
			_ => { },
			_ => Interlocked.Increment(ref failures),
			retryAfter: TimeSpan.FromSeconds(3));

		tail.Start();
		await Task.Delay(TimeSpan.FromMilliseconds(100));
		tail.Stop();

		Volatile.Read(ref reads).ShouldBeInRange(1, 2, "a failed read pauses before the next");
		Volatile.Read(ref failures).ShouldBeInRange(1, 2);
	}

	/// <summary>The tail reads from the stream's cursor, so each page asks only for what is new.</summary>
	[Test]
	public async Task The_tail_reads_from_the_cursor()
	{
		var stream = new SessionStream("s1");
		stream.Absorb(Page(1, 3, nextCursor: 9));
		var asked = new TaskCompletionSource<(long After, int Wait)>(TaskCreationOptions.RunContinuationsAsynchronously);

		var tail = stream.Tail(
			async (after, wait, cancellationToken) =>
			{
				asked.TrySetResult((after, wait));
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return Page(10, 1);
			},
			_ => { },
			_ => { });

		tail.Start();
		var first = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
		tail.Stop();

		first.ShouldBe((9L, 0), "from the cursor, and the first read waits for nothing");
	}

	/// <summary>
	/// What the stream remembers is bounded however long it is watched: calls and notes trimmed from the list are
	/// remembered only lately, enough that a read bringing the newest again does not take them back in.
	/// </summary>
	[Test]
	public void What_the_stream_remembers_is_bounded()
	{
		var stream = new SessionStream("s1");
		const int PageSize = 200;

		for (var first = 0; first < 10_000; first += PageSize)
		{
			var page = Enumerable.Range(first, PageSize).Select(index => Call($"c{index}", At.AddSeconds(index))).ToArray();
			stream.Absorb(Calls(page));
		}

		stream.Known.ShouldBeLessThanOrEqualTo(SessionStream.MaxRows + SessionStream.RememberedTrimmed);
		stream.Rows.Count.ShouldBe(SessionStream.MaxRows);

		// The newest page read again changes nothing, and neither does one just trimmed.
		var newest = Enumerable.Range(10_000 - PageSize, PageSize).Select(index => Call($"c{index}", At.AddSeconds(index))).ToArray();
		var trimmed = Enumerable.Range(10_000 - SessionStream.MaxRows - 50, 50).Select(index => Call($"c{index}", At.AddSeconds(index))).ToArray();
		var notice = stream.Notice;
		stream.Absorb(Calls(newest));
		stream.Absorb(Calls(trimmed));

		stream.Rows.Count.ShouldBe(SessionStream.MaxRows);
		stream.Rows[0].Key.ShouldBe($"c:c{10_000 - SessionStream.MaxRows}");
		stream.Notice.ShouldBe(notice, "nothing was taken back in and trimmed again");
	}

	private static List<string> Keys(SessionStream stream, StreamFilter filter)
	{
		stream.Filter = filter;
		return [.. stream.Rows.Select(row => row.Key)];
	}

	private static LiveDebugEvent Event(long sequence, LiveDebugEventKind kind, DateTime at) => new()
	{
		Sequence = sequence,
		TimestampUtc = at,
		Kind = kind,
		Message = $"event {sequence}",
	};

	private static LiveDebugEventPage Events(params LiveDebugEvent[] events) => new()
	{
		State = LiveAppSessionState.Ready,
		NextCursor = events.Max(entry => entry.Sequence),
		OldestAvailable = 1,
		TotalObserved = events.Max(entry => entry.Sequence),
		Events = events,
	};

	private static LiveDebugEventPage Page(
		long firstSequence,
		int count,
		long? nextCursor = null,
		long oldestAvailable = 1,
		long? totalObserved = null)
	{
		var events = Enumerable.Range(0, count).Select(offset => Event(firstSequence + offset, LiveDebugEventKind.LogMessage, At)).ToList();

		return new LiveDebugEventPage
		{
			State = LiveAppSessionState.Ready,
			NextCursor = nextCursor ?? firstSequence + count - 1,
			OldestAvailable = oldestAvailable,
			TotalObserved = totalObserved ?? firstSequence + count - 1,
			Events = events,
		};
	}

	private static ActivityEntry Call(string id, DateTime at, ActivityOutcome outcome = ActivityOutcome.Succeeded, string? error = null) => new()
	{
		Id = id,
		Kind = ActivityEntryKind.Call,
		Broker = "b1",
		StartedUtc = new DateTimeOffset(at),
		ClientId = "client-1",
		Origin = @"D:\Drawboard\PDF",
		DebugSession = "s1",
		Tool = "rose_debug_evaluate",
		Target = "value",
		Outcome = outcome,
		Error = error,
	};

	private static ActivityPage Calls(params ActivityEntry[] entries) => new()
	{
		Entries = [.. entries.Reverse()],
		Bodies = false,
		Matched = entries.Length,
		Retention = TimeSpan.FromDays(7),
	};

	private static NoteSubject Subject(string session) => new()
	{
		Kind = NoteSubjectKind.DebugValue,
		Readable = "_pen.Width = 0",
		Session = session,
		Path = "this._pen.Width",
	};

	private static AgentNote Note(string id, DateTime at, DateTime? replied = null) => new()
	{
		Id = id,
		ClientId = "client-1",
		ClientKey = "key-1",
		ClientFolder = @"D:\Drawboard\PDF",
		Subject = Subject("s1"),
		Text = "Is the pen getting the right width here?",
		PostedUtc = new DateTimeOffset(at),
		Delivery = replied is null ? NoteDelivery.Waiting : NoteDelivery.Delivered,
		DeliveredUtc = replied is { } answered ? new DateTimeOffset(answered.AddSeconds(-5)) : null,
		Replies = replied is { } when ? [new NoteReply { Text = "No, the zoom is 0.", RepliedUtc = new DateTimeOffset(when) }] : [],
	};

	private static NoteList Notes(params AgentNote[] notes) => new() { Notes = notes, Version = "v1" };
}
