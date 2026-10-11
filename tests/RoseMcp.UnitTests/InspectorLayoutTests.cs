using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// The Inspector's one view of the app, the agent and the person: what the header says, how any thread's stack is
/// read at a stop, what a watch shows, and what "Ask an agent" sends and to whom. All of it is what a person acts on,
/// so the wording is asserted, and so is every rule that keeps an answer from one stop off another.
/// </summary>
public sealed class InspectorLayoutTests
{
	private static readonly DateTime Now = DateTime.UtcNow;

	/// <summary>
	/// The header says who is in the session: the agent session that started it by name, or you and the one you
	/// shared it with, or you alone.
	/// </summary>
	[Test]
	public void The_header_says_who_is_in_the_session()
	{
		var agent = new LiveAppAgent { ClientId = "client-1", Name = @"D:\Drawboard\PDF", Connected = true };

		SessionRow.DescribeParties(SessionRowTests.Session() with { StartedByAgent = agent })
			.ShouldBe(@"In this session: you, and D:\Drawboard\PDF (started it)");
		SessionRow.DescribeParties(SessionRowTests.Session())
			.ShouldBe("In this session: you, and an agent session (started it)");
		SessionRow.DescribeParties(SessionRowTests.Session() with { StartedBy = LiveAppStarter.Person })
			.ShouldBe("In this session: you (started it), not shared with an agent");
		SessionRow.DescribeParties(SessionRowTests.Session() with { StartedBy = LiveAppStarter.Person, SharedWith = agent with { Connected = false } })
			.ShouldBe(@"In this session: you (started it), and D:\Drawboard\PDF (not connected)");

		var row = new SessionRow(SessionRowTests.Session() with { StartedByAgent = agent });
		row.Agents.ShouldHaveSingleItem().ShouldBe(agent);
	}

	/// <summary>
	/// "Live" while the host answers, and how long ago it last did once it has gone quiet past a few missed polls --
	/// a host that has stopped answering looks exactly like one with nothing new to say otherwise.
	/// </summary>
	[Test]
	public void The_header_says_how_current_it_is()
	{
		SessionRow.DescribeLiveness(LiveAppSessionState.Ready, TimeSpan.FromSeconds(1)).ShouldBe((true, "Live"));
		SessionRow.DescribeLiveness(LiveAppSessionState.Ready, TimeSpan.FromSeconds(14)).ShouldBe((false, "host last answered 14s ago"));
		SessionRow.DescribeLiveness(LiveAppSessionState.Ready, null).ShouldBe((false, "waiting for the host's first report"));
		SessionRow.DescribeLiveness(LiveAppSessionState.Ended, TimeSpan.Zero).Live.ShouldBeFalse();

		var row = new SessionRow(SessionRowTests.Session() with { InfoAge = TimeSpan.FromSeconds(1) });
		row.IsLive.ShouldBeTrue();

		row.Tick(DateTime.UtcNow.AddSeconds(20));
		row.IsLive.ShouldBeFalse("the age is counted on between polls");
		row.Liveness.ShouldStartWith("host last answered 2");
	}

	/// <summary>
	/// Why the app stopped, who stopped it and when it lets go: a breakpoint names who set it, a pause is yours, and a
	/// step names nobody rather than guessing.
	/// </summary>
	[Test]
	public void The_header_says_why_it_stopped_who_stopped_it_and_when_it_lets_go()
	{
		var stop = new LiveStop
		{
			State = LiveExecutionState.StoppedAtBreakpoint,
			BreakpointId = "bp-1",
			ThreadId = 13968,
			EventSequence = 12,
			StoppedAtUtc = Now,
			Resume = LiveStopResume.AutoContinue,
			ResumeDeadlineUtc = Now.AddSeconds(22),
		};
		var byAgent = SessionRowTests.Session(stop: stop) with
		{
			StopSetBy = new LiveBreakpointSetter { Person = false, Agent = new LiveAppAgent { ClientId = "client-1", Name = "PDF" } },
		};

		var row = new SessionRow(byAgent);

		row.StopTitle.ShouldBe("Stopped at a breakpoint");
		row.StopFacts.ShouldStartWith("set by PDF");
		row.StopFacts.ShouldContain("stopped ");
		row.StopFacts.ShouldContain("auto-continues in");
		row.StopThreadId.ShouldBe(13968);

		SessionRow.DescribeSetter(byAgent with { StopSetBy = new LiveBreakpointSetter { Person = true } }).ShouldBe("set by you");
		SessionRow.DescribeSetter(byAgent with { StopSetBy = new LiveBreakpointSetter { Person = false } }).ShouldBe("set by an agent session");
		SessionRow.DescribeSetter(byAgent with { Stop = stop with { State = LiveExecutionState.PausedByOperator }, StopSetBy = null }).ShouldBe("paused by you");
		SessionRow.DescribeSetter(byAgent with { Stop = stop with { State = LiveExecutionState.StoppedAtStep, BreakpointId = null }, StopSetBy = null })
			.ShouldBeEmpty();

		SessionRow.DescribeStop(byAgent with { Stop = stop with { State = LiveExecutionState.StoppedAtStep } }).ShouldBe("Stopped after a step");
		SessionRow.DescribeStop(SessionRowTests.Session()).ShouldBe("Running");
		new SessionRow(SessionRowTests.Session()).StopFacts.ShouldBeEmpty();
	}

	/// <summary>
	/// The line naming the app carries its framework, and the solution the agent in the session last worked in --
	/// said as where the agent works, since nothing in a debug session names one.
	/// </summary>
	[Test]
	public void The_app_line_names_its_framework_and_where_its_agent_works()
	{
		var row = new SessionRow(SessionRowTests.Session(stack: XamlStack.Uwp));
		row.AppLine.ShouldContain("pid 9191");
		row.AppLine.ShouldContain("UWP");

		ActivityEntry[] calls =
		[
			Entry("1", Now.AddMinutes(-9), @"D:\Old\Old.slnx"),
			Entry("2", Now.AddMinutes(-1), @"D:\Drawboard\PDF\Drawboard.All.slnx"),
			Entry("3", Now, workspace: null),
		];
		row.Learn(SessionRow.SolutionOf(calls));

		row.AppLine.ShouldEndWith(@"its agent works in Drawboard.All.slnx in D:\Drawboard\PDF");
		SessionRow.SolutionOf([]).ShouldBeNull();
		SessionRow.DescribeSolution(null).ShouldBeEmpty();
	}

	/// <summary>
	/// Any thread's stack can be read at a stop: the stopped thread's first, then whichever is chosen -- and an answer
	/// for another stop, or for a thread no longer chosen, is dropped rather than shown as this one's.
	/// </summary>
	[Test]
	public void Any_threads_stack_is_read_and_a_late_answer_is_dropped()
	{
		var code = new CodeInspection("s1", stopSequence: 77, stoppedThreadId: 9);

		var first = code.Take(Stack(77, 9, Frame(0, 9, "App.Worker.Apply", "Worker.cs", 88))).ShouldNotBeNull();
		code.Stack.ShouldBeSameAs(first);
		code.Location.ShouldBe("Worker.cs:88");

		code.Take(Threads(77, (9, true), (14, false))).ShouldBeTrue();
		code.SelectedThread.ShouldNotBeNull().Id.ShouldBe(9);
		code.StackCaption.ShouldBe("Stack · thread 9, where it stopped");

		var other = code.Threads[1];
		code.Choose(other).ShouldBeTrue();
		code.Stack.ShouldBeNull("the stopped thread's stack goes until the chosen one's is read");
		code.StackCaption.ShouldBe("Stack · thread 14");

		code.Take(Stack(77, 9, Frame(0, 9, "App.Worker.Apply", "Worker.cs", 88))).ShouldBeNull("that answer is for the thread no longer chosen");
		code.Take(Stack(78, 14, Frame(0, 14, "App.Pump.Wait", "Pump.cs", 12))).ShouldBeNull("that answer is for another stop");
		code.Take(Stack(77, 14, Frame(0, 14, "App.Pump.Wait", "Pump.cs", 12))).ShouldNotBeNull();

		code.Location.ShouldBe("Worker.cs:88", "the stop is still where the stopped thread is");
		code.Take(Threads(78, (9, true))).ShouldBeFalse();
		code.Threads.Count.ShouldBe(2);
	}

	/// <summary>
	/// A row from an earlier stop cannot be asked about at the next one: its frame index, thread position and value path
	/// name something else now, so the subject would send the agent to the wrong place. Neither can anything once the
	/// stop is over.
	/// </summary>
	[Test]
	public void A_row_from_an_earlier_stop_cannot_be_asked_about()
	{
		var earlier = new CodeInspection("s1", stopSequence: 77, stoppedThreadId: 9);
		var oldStack = earlier.Take(Stack(77, 9, Frame(0, 9, "App.Worker.Apply", "Worker.cs", 88))).ShouldNotBeNull();
		earlier.Take(Threads(77, (9, true)));
		oldStack.Show(oldStack.Frames[0], new LiveFrameVariables
		{
			Execution = LiveExecutionState.StoppedAtBreakpoint,
			Stop = StopAt(77),
			FrameIndex = 0,
			Variables = [new LiveVariable { Name = "value", Kind = "argument", Path = "arg:0", Value = "2.5", TypeName = "double", HasChildren = false }],
			Symbols = LiveSymbolState.Resolved,
			Truncated = false,
		});
		var oldThread = earlier.Threads[0];
		var oldFrame = oldStack.Frames[0];
		var oldValue = oldStack.Variables[0];

		earlier.SubjectOf(oldThread).ShouldNotBeNull();
		earlier.SubjectOf(oldFrame).ShouldNotBeNull();
		earlier.SubjectOf(oldValue).ShouldNotBeNull().Path.ShouldBe("arg:0");

		var current = new CodeInspection("s1", stopSequence: 78, stoppedThreadId: 9);
		current.Take(Stack(78, 9, Frame(0, 9, "App.Worker.Apply", "Worker.cs", 89)));
		current.Take(Threads(78, (9, true)));

		current.SubjectOf(oldThread).ShouldBeNull("that thread row was read at the last stop");
		current.SubjectOf(oldFrame).ShouldBeNull("that frame row was read at the last stop");
		current.SubjectOf(oldValue).ShouldBeNull("that value was read at the last stop");
		current.SubjectOf(current.Threads[0]).ShouldNotBeNull();

		earlier.End();
		earlier.SubjectOf(oldThread).ShouldBeNull("a stop that is over names nothing");
		earlier.SubjectOf(oldFrame).ShouldBeNull();
	}

	/// <summary>
	/// An ended session says the app exited, at the time its session recorded it, only where one was recorded; a host that
	/// died or a debugger detached elsewhere leaves the app running, so that is said as the session ending.
	/// </summary>
	[Test]
	public void An_ended_session_says_the_app_exited_only_where_it_did()
	{
		var exited = new DateTimeOffset(2026, 10, 12, 3, 4, 5, TimeSpan.Zero);
		var noticed = exited.AddSeconds(2);

		InspectorText.Ended(exited, noticed).ShouldBe($"The app exited at {exited.ToLocalTime():HH:mm:ss}");
		InspectorText.Ended(null, noticed).ShouldBe($"The session ended at {noticed.ToLocalTime():HH:mm:ss}");
		InspectorText.EndedLog(@"C:\logs\host.log").ShouldBe(@"Its log: C:\logs\host.log");
		InspectorText.EndedLog(null).ShouldStartWith("Its host named no log");
	}

	/// <summary>A watch takes a value path and refuses anything else, saying why, since the host would refuse it at every stop.</summary>
	[Test]
	public void A_watch_takes_a_value_path_and_refuses_anything_else()
	{
		var watches = new WatchList();

		watches.Add("this._pen.Width", out var problem).ShouldNotBeNull();
		problem.ShouldBeNull();

		watches.Add("local:x", out problem).ShouldBeNull();
		problem.ShouldNotBeNull().ShouldStartWith("Not a value path");
		watches.Add("  ", out problem).ShouldBeNull();
		problem.ShouldNotBeNull().ShouldStartWith("Not a value path");
		watches.Rows.ShouldHaveSingleItem().Detail.ShouldBe(WatchList.NotRead);
	}

	/// <summary>
	/// A watch reads at the frame being looked at, can then be asked about by its path, and goes blank the moment the
	/// target moves: a value read at one stop is a fact about that stop.
	/// </summary>
	[Test]
	public async Task A_watch_reads_at_the_frame_on_screen_and_goes_blank_when_the_target_moves()
	{
		var watches = new WatchList();
		var watch = watches.Add("this._zoomScale", out _).ShouldNotBeNull();
		var frame = new FrameRow(Frame(0, 9, "App.Worker.Apply", "Worker.cs", 88));
		var at = new WatchContext("s1", 77, 9, frame);
		(string Path, WatchContext At)? asked = null;

		await watches.ReadAsync(
			at,
			(path, context, _) =>
			{
				asked = (path, context);
				return Task.FromResult(Value(77, "0", "double"));
			},
			CancellationToken.None);

		asked.ShouldBe(("this._zoomScale", at));
		watch.Value.ShouldBe("0");
		watch.TypeName.ShouldBe("double");
		var subject = watch.Subject.ShouldNotBeNull();
		subject.Kind.ShouldBe(NoteSubjectKind.DebugValue);
		subject.Path.ShouldBe("this._zoomScale");
		subject.ThreadId.ShouldBe(9);
		subject.FrameIndex.ShouldBe(0);
		subject.Readable.ShouldBe("this._zoomScale = 0 (double) in App.Worker.Apply at line 88");

		watches.Stale();

		watch.Value.ShouldBeEmpty();
		watch.Subject.ShouldBeNull();
		watch.Detail.ShouldBe(WatchList.Running);
	}

	/// <summary>A read that lands after the target moved, or for another stop, is dropped rather than shown.</summary>
	[Test]
	public async Task A_watch_read_that_lands_late_is_dropped()
	{
		var watches = new WatchList();
		var watch = watches.Add("local:2", out _).ShouldNotBeNull();
		var answer = new TaskCompletionSource<LiveValueExpansion>();

		var reading = watches.ReadAsync(new WatchContext("s1", 77, null, null), (_, _, _) => answer.Task, CancellationToken.None);
		watches.Stale();
		answer.SetResult(Value(77, "3", "int"));
		await reading;

		watch.Value.ShouldBeEmpty("the target moved while it was being read");

		await watches.ReadAsync(new WatchContext("s1", 77, null, null), (_, _, _) => Task.FromResult(Value(78, "4", "int")), CancellationToken.None);

		watch.Value.ShouldBeEmpty("that answer was read at another stop");
		watch.Detail.ShouldBe(WatchList.Running);
	}

	/// <summary>
	/// Each row a person can select is made into a subject the agent can look up, with a line saying it in words: a
	/// thread, a frame, a value, a breakpoint and a tracepoint.
	/// </summary>
	[Test]
	public void Every_row_kind_makes_a_subject_the_agent_can_look_up()
	{
		var thread = new ThreadRow(new LiveThread { Id = 13968, IsStopped = true, TopFrame = "InkToolbarViewModel.ApplyStrokeWidth" });
		var frame = new FrameRow(Frame(1, 13968, "InkToolbarViewModel.OnWidthSliderChanged", "InkToolbarViewModel.cs", 61));
		var node = new VariableNode(new LiveVariable { Name = "strokeWidth", Kind = "local", TypeName = "double", Value = "0", Path = "local:1", HasChildren = false });

		var threadSubject = AskAgent.Thread("s1", thread);
		threadSubject.Kind.ShouldBe(NoteSubjectKind.Thread);
		threadSubject.ThreadId.ShouldBe(13968);
		threadSubject.Readable.ShouldBe("thread 13968, in InkToolbarViewModel.ApplyStrokeWidth");

		var frameSubject = AskAgent.Frame("s1", 13968, frame);
		frameSubject.FrameIndex.ShouldBe(1);
		frameSubject.Readable.ShouldBe("frame 1, InkToolbarViewModel.OnWidthSliderChanged at InkToolbarViewModel.cs:61 · IL_0010");

		var value = AskAgent.Value("s1", 13968, frame, node);
		value.Path.ShouldBe("local:1");
		value.Readable.ShouldBe("strokeWidth = 0 (double) in InkToolbarViewModel.OnWidthSliderChanged at line 61");

		var breakpoint = AskAgent.Breakpoint("s1", new BreakpointRow(new LiveBreakpoint
		{
			Id = "bp-3",
			Location = "InkToolbarViewModel.ApplyStrokeWidth",
			StopOnHit = true,
			Bound = true,
			HitCount = 1,
			AutoContinueSeconds = 30,
		}));
		breakpoint.Kind.ShouldBe(NoteSubjectKind.Breakpoint);
		breakpoint.Breakpoint.ShouldBe("bp-3");

		var tracepoint = AskAgent.Tracepoint("s1", new TracepointRow(new LiveTracepoint { Id = "tp-2", Location = "A.B.Beat", Bound = true, HitCount = 0 }));
		tracepoint.Breakpoint.ShouldBe("tp-2");
		tracepoint.Readable.ShouldStartWith("tracepoint tp-2");
	}

	/// <summary>
	/// A note about something in a debug session goes to the agent sessions in it that the table can tell apart; a
	/// person's session nobody shares says to share it, and an agent's whose session has gone says so.
	/// </summary>
	[Test]
	public void A_note_goes_to_the_agent_sessions_in_the_debug_session()
	{
		ClientSummary[] clients =
		[
			new() { Id = "client-1", Named = true, ConnectedUtc = DateTimeOffset.UnixEpoch, WorkingDirectory = @"D:\Drawboard\PDF" },
			new() { Id = "client-2", Named = true, ConnectedUtc = DateTimeOffset.UnixEpoch, WorkingDirectory = @"D:\Other" },
			new() { Id = "client-3", Named = false, ConnectedUtc = DateTimeOffset.UnixEpoch },
		];

		var (recipients, unreachable) = AskAgent.Recipients([new LiveAppAgent { ClientId = "client-1", Name = "PDF" }], persons: false, clients);
		recipients.ShouldHaveSingleItem().ShouldBe(new NoteRecipient("client-1", "PDF"));
		unreachable.ShouldBeEmpty();

		AskAgent.Recipients([], persons: true, clients).Unreachable.ShouldBe(AskAgent.NotShared);
		AskAgent.Recipients([], persons: false, clients).Unreachable.ShouldBe(AskAgent.AgentGone);
		AskAgent.Recipients([new LiveAppAgent { ClientId = "client-3" }], persons: true, clients).Recipients.ShouldBeEmpty("an unnamed client is never offered");
	}

	private static ActivityEntry Entry(string id, DateTime at, string? workspace) => new()
	{
		Id = id,
		Kind = ActivityEntryKind.Call,
		Broker = "b1",
		StartedUtc = new DateTimeOffset(at),
		Workspace = workspace,
	};

	private static LiveStackFrame Frame(int index, int thread, string method, string file, int line) => new()
	{
		Index = index,
		ThreadId = thread,
		MethodFullName = method,
		Location = method,
		IlOffset = 0x10,
		Mapping = LiveIlMapping.Exact,
		Symbols = LiveSymbolState.Resolved,
		Source = new LiveSourcePosition { File = file, Line = line, Column = 1, EndLine = line, EndColumn = 2 },
		IsActive = index == 0,
		SkippedBefore = 0,
	};

	private static LiveStop StopAt(long sequence) => new()
	{
		State = LiveExecutionState.StoppedAtBreakpoint,
		EventSequence = sequence,
		StoppedAtUtc = DateTime.UnixEpoch,
		Resume = LiveStopResume.AutoContinue,
		ResumeDeadlineUtc = DateTime.UnixEpoch.AddSeconds(30),
	};

	private static LiveStackFrames Stack(long sequence, int thread, params LiveStackFrame[] frames) => new()
	{
		Execution = LiveExecutionState.StoppedAtBreakpoint,
		Stop = StopAt(sequence),
		ThreadId = thread,
		Frames = frames,
		Offset = 0,
		Total = frames.Length,
		Truncated = false,
	};

	private static LiveThreadList Threads(long sequence, params (int Id, bool Stopped)[] threads) => new()
	{
		Execution = LiveExecutionState.StoppedAtBreakpoint,
		Stop = StopAt(sequence),
		Threads = [.. threads.Select(thread => new LiveThread { Id = thread.Id, IsStopped = thread.Stopped, TopFrame = $"Frame{thread.Id}" })],
	};

	private static LiveValueExpansion Value(long sequence, string value, string type) => new()
	{
		Execution = LiveExecutionState.StoppedAtBreakpoint,
		Stop = StopAt(sequence),
		Path = "p",
		Value = value,
		TypeName = type,
		Total = 0,
		Truncated = false,
	};
}
