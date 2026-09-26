using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The rows and sentences the inspector puts on screen.
/// <para>
/// Tested rather than eyeballed for the reason the tray's rows are: these are the strings a reader
/// acts on, and the distinctions in them -- a breakpoint that is unbound rather than broken, a tail
/// that is short rather than complete -- are exactly the ones a refactor flattens without anything
/// failing.
/// </para>
/// </summary>
public sealed class InspectorRowTests
{
	[Test]
	[Arguments(LiveDebugEventKind.ExceptionUnhandled, EventRow.EventTone.Critical)]
	[Arguments(LiveDebugEventKind.ProcessExited, EventRow.EventTone.Critical)]
	[Arguments(LiveDebugEventKind.ExceptionFirstChance, EventRow.EventTone.Caution)]
	[Arguments(LiveDebugEventKind.BreakpointHit, EventRow.EventTone.Notable)]
	[Arguments(LiveDebugEventKind.StepComplete, EventRow.EventTone.Notable)]
	[Arguments(LiveDebugEventKind.Paused, EventRow.EventTone.Notable)]
	[Arguments(LiveDebugEventKind.SessionNotice, EventRow.EventTone.Notable)]
	[Arguments(LiveDebugEventKind.ModuleLoaded, EventRow.EventTone.Neutral)]
	[Arguments(LiveDebugEventKind.LogMessage, EventRow.EventTone.Neutral)]
	public void An_events_tone_separates_the_ones_worth_scanning_for(LiveDebugEventKind kind, EventRow.EventTone tone)
	{
		EventRow.ToneOf(kind).ShouldBe(tone);
	}

	/// <summary>
	/// A first-chance exception and an unhandled one are a glance apart on screen, so their labels
	/// must not be. Every kind gets a label, including any added later.
	/// </summary>
	[Test]
	public void Every_kind_has_a_short_label_and_the_two_exception_kinds_differ()
	{
		EventRow.Label(LiveDebugEventKind.ExceptionUnhandled).ShouldNotBe(
			EventRow.Label(LiveDebugEventKind.ExceptionFirstChance));

		foreach (var kind in Enum.GetValues<LiveDebugEventKind>())
		{
			var label = EventRow.Label(kind);
			label.ShouldNotBeEmpty();
			(label.Length <= 12).ShouldBeTrue($"'{label}' is too long for a pill");
		}
	}

	[Test]
	public void An_expander_says_what_is_behind_it()
	{
		EventRow.DescribeDetail(0, 0).ShouldBe(string.Empty);
		EventRow.DescribeDetail(1, 0).ShouldBe("1 frame");
		EventRow.DescribeDetail(0, 3).ShouldBe("3 variables");
		EventRow.DescribeDetail(4, 2).ShouldBe("4 frames, 2 variables");

		// A tracepoint's values are the ones its message asked for, not everything in scope, and
		// "2 variables" on a method with nine reads as the other seven having gone missing.
		EventRow.DescribeDetail(0, 2, logged: true).ShouldBe("2 logged values");
	}

	/// <summary>
	/// A tracepoint's hit carries the values its message interpolated, and the row shows them behind
	/// the same expander a stop's frame uses -- a reader opening a row is asking what the values
	/// were, and which field the host filled is not their question.
	/// </summary>
	[Test]
	public void A_tracepoint_row_shows_what_its_message_logged()
	{
		var row = new EventRow(new LiveDebugEvent
		{
			Sequence = 42,
			TimestampUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
			Kind = LiveDebugEventKind.BreakpointHit,
			Message = "Tracepoint A.B.C hit #3 on thread 7: count=9",
			ThreadId = 7,
			Logged = [Variable("count")],
		});

		row.Logged.ShouldBeTrue();
		row.HasDetail.ShouldBeTrue();
		row.Variables.ShouldHaveSingleItem().Name.ShouldBe("count");
		row.DetailHeader.ShouldBe("1 logged value");
	}

	[Test]
	public void An_event_row_carries_what_the_host_captured()
	{
		var row = new EventRow(new LiveDebugEvent
		{
			Sequence = 41,
			TimestampUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
			Kind = LiveDebugEventKind.BreakpointHit,
			Message = "bp-1 stopped the target",
			ThreadId = 7,
			Frames = ["A.B.C", "A.B.Main"],
			Variables = [Variable("state")],
		});

		row.Sequence.ShouldBe(41);
		row.KindLabel.ShouldBe("breakpoint");
		row.Thread.ShouldBe("thread 7");
		row.HasThread.ShouldBeTrue();
		row.HasException.ShouldBeFalse();
		row.HasDetail.ShouldBeTrue();
		row.DetailHeader.ShouldBe("2 frames, 1 variable");
	}

	/// <summary>
	/// A breakpoint that has not bound is not broken -- its module may not be loaded -- so the row
	/// says which state it is in rather than colouring it as a failure.
	/// </summary>
	[Test]
	public void A_breakpoint_row_says_whether_it_is_bound_and_how_it_behaves()
	{
		var row = new BreakpointRow(new LiveBreakpoint
		{
			Id = "bp-1",
			Location = "A.B.C",
			StopOnHit = true,
			Bound = false,
			HitCount = 0,
			AutoContinueSeconds = 30,
			Condition = "count > 3",
			Detail = "no loaded module declares A.B.C",
		});

		row.Bound.ShouldBeFalse();
		row.BoundLabel.ShouldBe("not bound yet");
		row.Hits.ShouldBe("0 hits");
		row.HasDetail.ShouldBeTrue();
		row.Conditions.ShouldBe("when count > 3 · auto-continues after 30s");

		row.Update(new LiveBreakpoint
		{
			Id = "bp-1",
			Location = "A.B.C",
			StopOnHit = true,
			Bound = true,
			HitCount = 1,
			AutoContinueSeconds = 0,
		});

		row.Bound.ShouldBeTrue();
		row.BoundLabel.ShouldBe("bound");
		row.Hits.ShouldBe("1 hit");
		row.HasDetail.ShouldBeFalse();
		row.Conditions.ShouldBe("held until continued");
	}

	[Test]
	public void A_tracepoint_row_says_what_it_writes_and_how_often()
	{
		var row = new TracepointRow(new LiveTracepoint
		{
			Id = "tp-1",
			Location = "A.B.Beat",
			Bound = true,
			HitCount = 12,
			LogMessage = "beat",
			LogEveryNthHit = 10,
		});

		row.Behaviour.ShouldBe("every 10th hit · logs \"beat\"");
		row.Hits.ShouldBe("12 hits");
	}

	[Test]
	[Arguments(1, "1st")]
	[Arguments(2, "2nd")]
	[Arguments(3, "3rd")]
	[Arguments(4, "4th")]
	[Arguments(11, "11th")]
	[Arguments(12, "12th")]
	[Arguments(13, "13th")]
	[Arguments(21, "21st")]
	[Arguments(102, "102nd")]
	[Arguments(111, "111th")]
	public void Ordinals_read_the_way_english_does(int value, string expected)
	{
		TracepointRow.Ordinal(value).ShouldBe(expected);
	}

	/// <summary>
	/// A tail is bounded, and what it trims is said. A silently truncated tail reads as a complete
	/// one, which is the difference between "nothing happened before this" and "I stopped looking".
	/// </summary>
	[Test]
	public void The_tail_is_capped_and_says_what_it_dropped()
	{
		var session = new InspectedSession("s1");

		session.Absorb(Page(1, InspectedSession.MaxEvents + 5));

		session.Events.Count.ShouldBe(InspectedSession.MaxEvents);
		session.HasNotice.ShouldBeTrue();
		session.Notice.ShouldContain("5 earlier events", Case.Sensitive);

		// The oldest went, so the newest is what is left at the end.
		session.Events[^1].Sequence.ShouldBe(InspectedSession.MaxEvents + 5);
	}

	/// <summary>
	/// The cursor advances so the next poll asks only for what is new. Reading it back from the
	/// page rather than from the last event is what makes a filtered read page forward at all.
	/// </summary>
	[Test]
	public void Absorbing_a_page_advances_the_cursor()
	{
		var session = new InspectedSession("s1");

		session.Absorb(Page(1, 3, nextCursor: 9));

		session.Cursor.ShouldBe(9);
		session.Events.Count.ShouldBe(3);
		session.HasEvents.ShouldBeTrue();
		session.HasNotice.ShouldBeFalse();
	}

	/// <summary>
	/// The host's buffer is bounded too, so a reader that fell behind missed events nobody will
	/// ever see. The count of them is the only thing that says the tail has a hole in it.
	/// </summary>
	[Test]
	public void Events_the_host_dropped_are_counted()
	{
		var session = new InspectedSession("s1");
		session.Absorb(Page(1, 2, nextCursor: 2, oldestAvailable: 1, totalObserved: 2));

		// The next page starts at 20: sequences 3 to 19 went out of the host's buffer.
		session.Absorb(Page(20, 1, nextCursor: 20, oldestAvailable: 20, totalObserved: 20));

		session.HasNotice.ShouldBeTrue();
		session.Notice.ShouldContain("17 earlier events", Case.Sensitive);
	}

	/// <summary>Breakpoint rows survive a poll, so the row a reader is about to click stays put.</summary>
	[Test]
	public void Breakpoints_merge_in_place()
	{
		var session = new InspectedSession("s1");
		session.Absorb(new LiveBreakpointList { Breakpoints = [Breakpoint("bp-1", bound: false)] });

		var row = session.Breakpoints[0];
		session.Absorb(new LiveBreakpointList { Breakpoints = [Breakpoint("bp-1", bound: true)] });

		session.Breakpoints[0].ShouldBeSameAs(row);
		row.Bound.ShouldBeTrue();
	}

	private static LiveBreakpoint Breakpoint(string id, bool bound) => new()
	{
		Id = id,
		Location = "A.B.C",
		StopOnHit = true,
		Bound = bound,
		HitCount = 0,
		AutoContinueSeconds = 30,
	};

	private static LiveVariable Variable(string name) => new()
	{
		Name = name,
		Kind = "argument",
		Path = "arg:0",
		HasChildren = false,
	};

	private static LiveDebugEventPage Page(
		long firstSequence,
		int count,
		long? nextCursor = null,
		long oldestAvailable = 1,
		long? totalObserved = null)
	{
		var events = Enumerable.Range(0, count).Select(offset => new LiveDebugEvent
		{
			Sequence = firstSequence + offset,
			TimestampUtc = DateTime.UtcNow,
			Kind = LiveDebugEventKind.LogMessage,
			Message = $"event {firstSequence + offset}",
		}).ToList();

		return new LiveDebugEventPage
		{
			State = LiveAppSessionState.Ready,
			NextCursor = nextCursor ?? firstSequence + count - 1,
			OldestAvailable = oldestAvailable,
			TotalObserved = totalObserved ?? firstSequence + count - 1,
			Events = events,
		};
	}
}
