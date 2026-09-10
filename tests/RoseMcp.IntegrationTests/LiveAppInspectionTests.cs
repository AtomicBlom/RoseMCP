using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a session says about itself, and what it says about a stop, read the way an operator view
/// reads it.
/// <para>
/// Every test here drives the plain .NET probe target rather than a XAML app, so none of them takes
/// a lease on a single-instance package and they run alongside the rest of the suite. That is also
/// the interesting case for the self-report: a target that is not a XAML app has to say so, with a
/// reason, rather than leaving a reader to conclude it from an absence.
/// </para>
/// </summary>
public sealed class LiveAppInspectionTests
{
	/// <summary>
	/// The fields an operator view acts on, from a target that is not a XAML app at all. Each of
	/// them is a fact the session lifecycle cannot carry: a Ready session may be running or held, and
	/// one whose host has stopped answering goes on describing itself accurately as of some moment.
	/// </summary>
	[Test]
	public async Task The_self_report_names_the_stack_the_provider_and_the_heartbeat()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);

			// The report comes from a poll, so the first one is what is being waited for here.
			var summary = await WaitForSummaryAsync(session, described => described.InfoAge is not null, cancellationToken);
			Assert.NotNull(summary);

			Assert.Equal(LiveAppSessionState.Ready, summary!.State);
			Assert.Equal(LiveExecutionState.Running, summary.Execution);
			Assert.Null(summary.Stop);

			// A console app is no XAML framework, and the reason names what was read to decide that.
			Assert.Equal(XamlStack.Unknown, summary.XamlStack);
			Assert.Contains("loaded modules", summary.XamlStackReason);
			Assert.Equal(LiveXamlProvider.None, summary.XamlProvider);

			// The log that explains this session, so a reader has somewhere to go.
			Assert.NotNull(summary.HostLogPath);
			Assert.EndsWith(".log", summary.HostLogPath);

			// The probe throws every 200ms, so a heartbeat this stale means the event stream has died.
			var heartbeat = await WaitForSummaryAsync(session, described => described.LastEventAge is not null, cancellationToken);
			Assert.NotNull(heartbeat);
			Assert.True(
				heartbeat!.LastEventAge < TimeSpan.FromSeconds(30),
				$"the target last spoke {heartbeat.LastEventAge} ago");

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A session's calls are recorded the way a worker's are, which is what gives an operator view
	/// anything to show. The self-report is deliberately not among them: it runs every second, and
	/// recording it would fill the recent list with the poll that reads the recent list.
	/// </summary>
	[Test]
	public async Task A_sessions_calls_are_recorded_as_activities_and_its_own_poll_is_not()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);
			await session.SetBreakpointAsync("DebugProbeTarget.Program.Beat", autoContinueSeconds: 1, condition: null, cancellationToken);

			var summary = session.Describe();
			var recorded = summary.Recent.FirstOrDefault(activity => activity.Operation == ToolNames.LiveAppSetBreakpoint);
			Assert.NotNull(recorded);
			Assert.Equal(ActivityOutcome.Succeeded, recorded!.Outcome);

			// The argument that says which call this was, rather than only which tool.
			Assert.Contains("Program.Beat", recorded.Target);

			var polls = summary.Recent.Concat(summary.Running).Where(activity => activity.Operation == ToolNames.LiveAppInfo);
			Assert.Empty(polls);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A stop's locals carry the names the code declares, read from the module's portable PDB, and
	/// the evaluator resolves a caller who passes one of them back.
	/// <para>
	/// The names are the whole point: a caller told <c>local_0</c> has to count declarations to guess
	/// which variable that is, and the guess is silent when it is wrong. So both halves are asserted,
	/// because reporting a name the evaluator cannot then resolve would be worse than reporting the
	/// slot.
	/// </para>
	/// </summary>
	[Test]
	public async Task Locals_at_a_stop_carry_their_source_names()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);

			var breakpoint = await session.SetBreakpointAsync(
				"DebugProbeTarget.Program.Inspect", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);
			Assert.NotNull(stop!.Variables);

			// The argument is named from metadata, as it always was.
			var argument = stop.Variables!.FirstOrDefault(variable => variable.Name == "state");
			Assert.NotNull(argument);
			Assert.Equal("argument", argument!.Kind);

			// The local is named from the PDB. Slot names are the fallback, so one appearing here is
			// the reader having found no symbols rather than a different name.
			var local = stop.Variables!.FirstOrDefault(variable => variable.Kind == "local");
			Assert.NotNull(local);
			Assert.Equal("innerCount", local!.Name);
			Assert.Equal("int", local.TypeName);

			// And the name resolves back, which is what makes it worth reporting.
			var evaluated = await session.EvaluateAsync("innerCount", cancellationToken);
			Assert.True(evaluated.Error is null, $"innerCount should evaluate; error: {evaluated.Error}");
			Assert.Equal("int", evaluated.TypeName);

			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.ContinueAsync(cancellationToken));
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target is still running after being read");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A structured stack, which is what a person reads rather than the flat strings an event
	/// carries: frame 0 is the method the breakpoint bound to, with the file and line its symbols
	/// give, and frame 1 is what called it.
	/// <para>
	/// The stop's own sequence is echoed on the answer. That is what lets a reader tell a stack read
	/// at this stop from one read at the last, and everything a debugger hands out -- frames, values,
	/// threads -- is valid only within the stop it came from.
	/// </para>
	/// </summary>
	[Test]
	public async Task Frames_at_a_stop_carry_file_and_line()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);
			var stop = await StopOnInspectAsync(session, cancellationToken);

			var frames = await session.ReadFramesAsync(null, 0, null, cancellationToken);

			Assert.Equal(LiveExecutionState.StoppedAtBreakpoint, frames.Execution);
			Assert.NotNull(frames.Stop);
			Assert.Equal(stop.Hit.Sequence, frames.Stop!.EventSequence);
			Assert.True(frames.Frames.Count >= 2, $"expected Inspect and its caller, got {frames.Frames.Count} frame(s)");

			var inspect = frames.Frames[0];
			Assert.Equal(0, inspect.Index);
			Assert.True(inspect.IsActive, "frame 0 of the held thread is the active frame");
			Assert.Equal("DebugProbeTarget.Program.Inspect", inspect.MethodFullName);
			Assert.Equal("DebugProbeTarget.dll", inspect.Module);
			Assert.Equal(LiveSymbolState.Resolved, inspect.Symbols);
			Assert.NotNull(inspect.Source);
			Assert.EndsWith("Program.cs", inspect.Source!.File);
			Assert.True(inspect.Source.Line > 0, "a resolved frame has a real line");

			// The caller, which is what makes a stack worth reading rather than one frame.
			Assert.Equal("DebugProbeTarget.Program.Main", frames.Frames[1].MethodFullName);
			Assert.False(frames.Frames[1].IsActive);

			// Paging is over the same walk, so an offset picks up where the first page's index left off.
			var second = await session.ReadFramesAsync(null, 1, 1, cancellationToken);
			Assert.Equal(1, second.Offset);
			Assert.Equal(frames.Total, second.Total);
			Assert.Equal("DebugProbeTarget.Program.Main", Assert.Single(second.Frames).MethodFullName);

			await session.RemoveBreakpointAsync(stop.BreakpointId, cancellationToken);
			await session.ResumeAsync(cancellationToken);
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Walking an object graph by path: a frame's variables carry the path that expands each of
	/// them, expanding gives children carrying theirs, and a path a caller composes reaches the same
	/// value. A path that names nothing says which step failed rather than answering emptily.
	/// </summary>
	[Test]
	public async Task A_value_expands_by_path()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);
			var stop = await StopOnInspectAsync(session, cancellationToken);

			var frame = await session.ReadFrameVariablesAsync(0, null, cancellationToken);
			Assert.Equal(LiveExecutionState.StoppedAtBreakpoint, frame.Execution);
			Assert.Equal("DebugProbeTarget.Program.Inspect", frame.MethodFullName);
			Assert.Equal(LiveSymbolState.Resolved, frame.Symbols);

			var state = frame.Variables.First(variable => variable.Name == "state");
			Assert.Equal("arg:0", state.Path);
			Assert.True(state.HasChildren, "an object argument is expandable");

			// A primitive local is not, which is what stops a tree offering an expander onto nothing.
			var innerCount = frame.Variables.First(variable => variable.Name == "innerCount");
			Assert.Equal("local:0", innerCount.Path);
			Assert.False(innerCount.HasChildren);

			// The object's own fields, read from memory. No property getter runs, so what is listed
			// is what the object holds.
			var fields = await session.ExpandValueAsync(state.Path, 0, null, cancellationToken);
			Assert.Equal("DebugProbeTarget.ProbeState", fields.TypeName);
			Assert.Contains(fields.Children, child => child.Name == "Count" && child.Kind == "field");
			Assert.Contains(fields.Children, child => child.Name == "Label" && child.Value == "\"beat\"");

			var marks = fields.Children.First(child => child.Name == "Marks");
			Assert.Equal("arg:0.Marks", marks.Path);
			Assert.True(marks.HasChildren, "a non-empty array is expandable");

			// An array expands to elements, each addressed by index.
			var elements = await session.ExpandValueAsync(marks.Path, 0, null, cancellationToken);
			Assert.Equal(3, elements.Total);
			Assert.False(elements.Truncated);
			Assert.Equal(["[0]", "[1]", "[2]"], elements.Children.Select(element => element.Name));
			Assert.Equal("8", elements.Children[1].Value);
			Assert.Equal("arg:0.Marks[1]", elements.Children[1].Path);

			// A path composed rather than echoed reaches the same value, which is the round trip the
			// grammar exists for.
			var nested = await session.ExpandValueAsync("arg:0.Inner", 0, null, cancellationToken);
			Assert.Contains(nested.Children, child => child.Name == "Count" && child.Value == "-1");

			// And a step that resolves nothing names itself rather than reporting an empty value. The
			// type is whatever the boundary converted it to; what matters is that the sentence survived.
			var refusal = await Assert.ThrowsAnyAsync<Exception>(
				async () => await session.ExpandValueAsync("arg:0.Nope", 0, null, cancellationToken));
			Assert.Contains("Nope", refusal.Message);

			await session.RemoveBreakpointAsync(stop.BreakpointId, cancellationToken);
			await session.ResumeAsync(cancellationToken);
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A hold suspends the safety timer, which is the whole point of it: a person reading a stack
	/// must not have it move under them two seconds in. The breakpoint here asks for a two-second
	/// auto-continue and the target is still held well past it.
	/// </summary>
	[Test]
	public async Task An_operator_hold_suspends_auto_continue()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);

			var breakpoint = await session.SetBreakpointAsync(
				"DebugProbeTarget.Program.Inspect", autoContinueSeconds: 2, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");

			var hit = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(hit);

			var held = await session.HoldAsync(20, release: false, cancellationToken);
			Assert.True(held.Applied);
			Assert.NotNull(held.Stop);
			Assert.Equal(LiveStopResume.HeldByOperator, held.Stop!.Resume);

			// Well past the two seconds the breakpoint asked for, and still held.
			await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);

			var frames = await session.ReadFramesAsync(null, 0, null, cancellationToken);
			Assert.Equal(LiveExecutionState.StoppedAtBreakpoint, frames.Execution);
			Assert.Equal(LiveStopResume.HeldByOperator, frames.Stop!.Resume);

			// Releasing gives the stop back to the timer, which then fires within its own interval.
			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			var released = await session.HoldAsync(null, release: true, cancellationToken);
			Assert.True(released.Applied);

			var resumed = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.SessionNotice && entry.Message.Contains("Auto-continued"),
				cancellationToken,
				startCursor: hit!.Sequence);
			Assert.NotNull(resumed);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target is still running after being held and released");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// An agent's continue during a person's hold succeeds and says so. Refusing would be worse -- a
	/// continue means continue -- but a hold that vanishes silently is a reader's stack disappearing
	/// with nothing to explain it, which is the failure this sentence exists to prevent.
	/// </summary>
	[Test]
	public async Task A_continue_during_a_hold_releases_it_loudly()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);
			var stop = await StopOnInspectAsync(session, cancellationToken);

			var held = await session.HoldAsync(60, release: false, cancellationToken);
			Assert.True(held.Applied);

			await session.RemoveBreakpointAsync(stop.BreakpointId, cancellationToken);
			var resumed = await session.ResumeAsync(cancellationToken);

			Assert.True(resumed.Continued);
			Assert.NotNull(resumed.Detail);
			Assert.Contains("hold", resumed.Detail!, StringComparison.OrdinalIgnoreCase);

			// And the target really is running again, not merely reported as such.
			var frames = await session.ReadFramesAsync(null, 0, null, cancellationToken);
			Assert.Equal(LiveExecutionState.Running, frames.Execution);
			Assert.Null(frames.Stop);
			Assert.Empty(frames.Frames);
			Assert.NotNull(frames.Detail);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Threads are readable at a stop and not while running, and the running case says so rather
	/// than refusing: enumerating threads needs the runtime synchronized, and synchronizing it to
	/// answer a question nobody asked would stop somebody's application.
	/// </summary>
	[Test]
	public async Task Threads_are_listed_at_a_stop_and_reported_as_unreadable_while_running()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);

			var running = await session.ReadThreadsAsync(cancellationToken);
			Assert.Equal(LiveExecutionState.Running, running.Execution);
			Assert.Empty(running.Threads);
			Assert.NotNull(running.Detail);

			var stop = await StopOnInspectAsync(session, cancellationToken);

			var stopped = await session.ReadThreadsAsync(cancellationToken);
			Assert.Equal(LiveExecutionState.StoppedAtBreakpoint, stopped.Execution);
			Assert.NotEmpty(stopped.Threads);

			// The held thread is first, and it is the one the stop names.
			var first = stopped.Threads[0];
			Assert.True(first.IsStopped, "the held thread leads the list");
			Assert.Equal(stopped.Stop!.ThreadId, first.Id);
			Assert.Equal("DebugProbeTarget.Program.Inspect", first.TopFrame);

			// Only one thread is the held one, whatever else the runtime is running.
			Assert.Single(stopped.Threads.Where(thread => thread.IsStopped));

			await session.RemoveBreakpointAsync(stop.BreakpointId, cancellationToken);
			await session.ResumeAsync(cancellationToken);
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Sets a breakpoint on the probe's <c>Inspect</c> and waits for the hit that holds the target.
	/// Both halves come back: the event's sequence identifies the stop, and the breakpoint's id is
	/// what the caller removes before resuming so the loop does not stop again immediately.
	/// </summary>
	private static async Task<(LiveDebugEvent Hit, string BreakpointId)> StopOnInspectAsync(
		LiveAppSession session,
		CancellationToken cancellationToken)
	{
		var breakpoint = await session.SetBreakpointAsync(
			"DebugProbeTarget.Program.Inspect", autoContinueSeconds: null, condition: null, cancellationToken);
		Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");

		var hit = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
			cancellationToken);
		Assert.NotNull(hit);

		return (hit!, breakpoint.Id);
	}

	private static LiveAppTarget Attach(int processId) => new()
	{
		Kind = LiveAppTargetKind.AttachProcess,
		ProcessId = processId,
		Description = "probe target",
	};

	/// <summary>
	/// Reads the session's own description until it matches, or ten seconds pass. The report is filled
	/// by a poll on its own timer, so what a caller sees immediately after attaching is a session that
	/// has not been asked yet.
	/// </summary>
	private static async Task<LiveAppSessionSummary?> WaitForSummaryAsync(
		LiveAppSession session,
		Func<LiveAppSessionSummary, bool> match,
		CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

		while (DateTime.UtcNow < deadline)
		{
			var summary = session.Describe();
			if (match(summary)) return summary;

			await Task.Delay(200, cancellationToken);
		}

		return null;
	}
}
