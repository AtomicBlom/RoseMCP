using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;


namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a session can do once the target is running: binding a breakpoint by name, logging from a
/// tracepoint, holding the target and letting it go again, gating a stop on a condition, reading a
/// value at that stop, and stepping from it.
/// <para>
/// A stop is the thing these are really about. Everything here either reaches one, does something
/// while the target is held, or leaves it running afterwards -- and the target being left usable is
/// asserted as often as the stop itself, because a debugger that reads the right value and wedges the
/// process has not worked.
/// </para>
/// </summary>
[Category("LiveApp")]
public sealed class LiveAppDebugTests
{
	/// <summary>
	/// A method named by <c>Namespace.Type.Method</c> alone binds in the module that declares its type,
	/// whatever that module is called. <c>Elsewhere.Pulse</c> is compiled into <c>DebugProbeTarget.dll</c>,
	/// so no segment of the name is a module -- the shape of a repository that names its assemblies and
	/// its namespaces apart, where a module read off the name is a module that does not exist. The hit is
	/// waited for as well, because a hit is matched back to its breakpoint by the module it bound in.
	/// </summary>
	[Test]
	public async Task A_bare_name_binds_in_the_module_that_declares_its_type()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);

			var source = await session.ReadMethodSourceAsync("Elsewhere.Pulse.Tick", cancellationToken);
			Assert.Equal("DebugProbeTarget", source.Module);

			var breakpoint = await session.SetBreakpointAsync("Elsewhere.Pulse.Tick", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"a bare name should bind in the module declaring its type; detail: {breakpoint.Detail}");

			var hit = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("Elsewhere.Pulse.Tick"),
				cancellationToken);
			Assert.NotNull(hit);

			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.ContinueAsync(cancellationToken));
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A tracepoint (issue #17): set at a method by name, it binds to the already-loaded module,
	/// records each hit in the event stream with its message, never pauses the target, and can be
	/// removed. This is the low-friction, non-freezing default for a turn-based agent.
	/// </summary>
	[Test]
	public async Task Tracepoint_binds_logs_hits_and_can_be_removed()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "probe target",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var tracepoint = await session.AddTracepointAsync(
				"DebugProbeTarget.Program.Beat", "beat", logEveryNthHit: null, condition: null, cancellationToken);
			Assert.True(tracepoint.Bound, $"tracepoint should bind against the loaded module; detail: {tracepoint.Detail}");

			var hit = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit,
				cancellationToken);
			Assert.NotNull(hit);
			Assert.Contains("beat", hit!.Message);

			// Filtering by kind, over the wire, because that is where it has to work. A freshly
			// started app buffers hundreds of ModuleLoaded events, and a caller after the tracepoint
			// hits should not have to pull all of them across to find one.
			var unfiltered = await session.ReadEventsAsync(0, cancellationToken);
			Assert.Contains(unfiltered.Events, entry => entry.Kind == LiveDebugEventKind.ModuleLoaded);
			Assert.Equal(0, unfiltered.Skipped);

			var hitsOnly = await session.ReadEventsAsync(0, ["BreakpointHit"], limit: 500, cancellationToken);
			Assert.NotEmpty(hitsOnly.Events);
			Assert.All(hitsOnly.Events, entry => Assert.Equal(LiveDebugEventKind.BreakpointHit, entry.Kind));

			// The two things that make a filter usable rather than a trap: it says how much it passed
			// over, and paging with its cursor moves forward instead of re-reading forever.
			Assert.True(hitsOnly.Skipped > 0, "the filter should report the events it passed over");

			var lastRead = hitsOnly.Events[^1].Sequence;

			// At or past, never strictly past. NextCursor is how far reading got, so it equals the last
			// returned sequence whenever the newest event examined was one the filter matched -- a fact
			// about what the target emitted in the last millisecond rather than about the contract.
			// Asserting strictly greater is asserting that the last event examined was skipped, which is
			// a coin toss against a target emitting continuously, and says nothing about paging.
			Assert.True(
				hitsOnly.NextCursor >= lastRead,
				$"the filtered cursor ({hitsOnly.NextCursor}) should be at or past the last event it returned ({lastRead})");

			var nextPage = await session.ReadEventsAsync(hitsOnly.NextCursor, ["BreakpointHit"], limit: 500, cancellationToken);

			// Paging forward is the property, and this checks it directly: reading is exclusive of the
			// cursor, so nothing already returned can come back whether the two were equal or not.
			Assert.DoesNotContain(nextPage.Events, entry => entry.Sequence <= lastRead);

			// An unrecognised kind narrows to nothing rather than silently widening to everything.
			var nonsense = await Assert.ThrowsAsync<InvalidOperationException>(
				() => session.ReadEventsAsync(0, ["NotAKind"], limit: 500, cancellationToken));

			Assert.Contains("Unknown event kind 'NotAKind'", nonsense.Message, StringComparison.Ordinal);

			var remaining = await session.RemoveTracepointAsync(tracepoint.Id, cancellationToken);
			Assert.Empty(remaining.Tracepoints);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target runs on through the tracepoint");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A stopping breakpoint (issue #6): set at a method by name, it holds the target on hit and
	/// records the stop with its stack; continuing resumes it, and detach leaves it running. This is
	/// the interactive counterpart to a tracepoint.
	/// </summary>
	[Test]
	public async Task Stopping_breakpoint_holds_the_target_and_continue_resumes_it()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "probe target",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var breakpoint = await session.SetBreakpointAsync("DebugProbeTarget.Program.Beat", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind against the loaded module; detail: {breakpoint.Detail}");

			// The hit holds the target and records the stop with a stack that names the method.
			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);
			Assert.NotNull(stop!.Frames);
			Assert.Contains(stop.Frames!, frame => frame.Contains("DebugProbeTarget.Program.Beat"));

			// The stop captured the top frame's arguments (#7): Beat(int iteration).
			Assert.NotNull(stop.Variables);
			var iteration = stop.Variables!.FirstOrDefault(variable => variable.Name == "iteration");
			Assert.NotNull(iteration);
			Assert.Equal("argument", iteration!.Kind);
			Assert.Equal("int", iteration.TypeName);
			Assert.True(int.TryParse(iteration.Value, out _), $"expected an int value, got '{iteration.Value}'");

			// Remove the breakpoint so continuing does not immediately re-stop, then resume.
			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.ContinueAsync(cancellationToken));

			// Resumed: the loop runs past the (now removed) breakpoint and throws again, after the stop.
			var afterResume = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseDebugProbeException") ?? false)
					&& entry.Sequence > stop.Sequence,
				cancellationToken,
				startCursor: stop.Sequence);
			Assert.NotNull(afterResume);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "a stop holds the target rather than killing it");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A conditional breakpoint (issue #17): a cheap value-compare gates each hit, so the target is only
	/// held once the condition holds. The probe increments its argument each loop, so a stop at a value
	/// well above zero proves every earlier hit was skipped.
	/// </summary>
	[Test]
	public async Task Conditional_breakpoint_stops_only_when_the_condition_holds()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "probe target",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			// An ordering rather than an equality, because the probe counts up every 200ms and never
			// repeats a value: "iteration == 30" is satisfiable for 200ms exactly, and a machine busy
			// enough to spend that long between starting the probe and binding the breakpoint can never
			// satisfy it again -- so the test waits out its timeout on a condition that cannot hold.
			// The gating is still what is proved, and proved more strongly: an ungated breakpoint stops
			// on the first hit, which is zero.
			var breakpoint = await session.SetBreakpointAsync(
				"DebugProbeTarget.Program.Beat", autoContinueSeconds: null, condition: "iteration >= 30", cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");
			Assert.Equal("iteration >= 30", breakpoint.Condition);

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);

			// Every hit below the conditioned value was gated out, which is the whole claim: an
			// unconditional breakpoint on this method stops at zero.
			var iteration = stop!.Variables!.First(variable => variable.Name == "iteration");
			Assert.True(
				int.TryParse(iteration.Value, out var reached) && reached >= 30,
				$"stopped at iteration '{iteration.Value}', so hits below 30 were not gated out");

			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			await session.ContinueAsync(cancellationToken);
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target is still running after a conditional stop");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Field-access evaluation at a stop (issue #7): held at a breakpoint on Inspect(ProbeState), drill
	/// into the argument's object graph -- <c>state.Label</c> and <c>state.Inner.Count</c> -- reading
	/// fields directly, no debuggee code run. A missing field is a clean error, not a throw.
	/// </summary>
	[Test]
	public async Task Evaluates_a_field_access_expression_at_a_stop()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "probe target",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var breakpoint = await session.SetBreakpointAsync("DebugProbeTarget.Program.Inspect", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);

			// A field on the argument, and a two-level chain into the graph.
			var label = await session.EvaluateAsync("state.Label", cancellationToken);
			Assert.True(label.Error is null, $"state.Label should evaluate; error: {label.Error}");
			Assert.Equal("string", label.TypeName);
			Assert.Equal("\"beat\"", label.Value);

			var innerCount = await session.EvaluateAsync("state.Inner.Count", cancellationToken);
			Assert.Null(innerCount.Error);
			Assert.Equal("int", innerCount.TypeName);
			Assert.Equal("-1", innerCount.Value);

			// A field that does not exist reports why rather than throwing.
			var missing = await session.EvaluateAsync("state.Nope", cancellationToken);
			Assert.NotNull(missing.Error);

			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.ContinueAsync(cancellationToken));
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target is still running after an evaluation");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Stepping (issue #6): once held at a breakpoint, a step resumes the target briefly and holds it
	/// again at the next location, which arrives as a StepComplete event with a fresh stack.
	/// </summary>
	[Test]
	public async Task Step_from_a_stop_lands_a_step_complete_with_a_stack()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "probe target",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var breakpoint = await session.SetBreakpointAsync("DebugProbeTarget.Program.Beat", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);

			// Remove the breakpoint so only the step holds the target, then step.
			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.StepAsync("over", cancellationToken));

			var stepComplete = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.StepComplete && entry.Sequence > stop!.Sequence,
				cancellationToken,
				startCursor: stop!.Sequence);
			Assert.NotNull(stepComplete);
			Assert.NotNull(stepComplete!.Frames);
			Assert.Contains(stepComplete.Frames!, frame => frame.Contains("DebugProbeTarget.Program"));

			// Release the step hold and confirm the target keeps running.
			await session.ContinueAsync(cancellationToken);
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target is still running after a step");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A target that dies while the debugger holds it stops being stopped. The stop went with the
	/// process, and a session still reporting one sends a reader to resume something that is not
	/// there.
	/// <para>
	/// Asserted through the XAML tree rather than the session summary. Every XAML verb refuses while
	/// the target is stopped and names the stop as the reason, so it reads the host's own answer --
	/// whereas the summary substitutes a running target for a stop on an ended session and would pass
	/// whatever the host reported.
	/// </para>
	/// <para>
	/// The probe is a console app with no XAML in it, which is what makes this cheap: the refusal is
	/// checked before anything goes looking for a provider, so reaching it needs a stop and a target
	/// and nothing else.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_target_that_dies_while_held_is_no_longer_reported_as_stopped()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			// Long enough that the safety timer cannot be what ends this stop: the point is a target
			// that dies while still held, so a stop released on its own would prove nothing.
			var breakpoint = await session.SetBreakpointAsync(
				"DebugProbeTarget.Program.Beat", autoContinueSeconds: 300, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind against the loaded module; detail: {breakpoint.Detail}");

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);

			// While the target is there, the stop is the reason a XAML read cannot be served.
			var held = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Contains("The target is stopped", held.Detail ?? string.Empty, StringComparison.Ordinal);

			child.Kill(entireProcessTree: true);
			child.WaitForExit(10_000);

			// The exit arrives on mscordbi's thread, so it is polled for rather than assumed.
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
			var gone = held;
			while (DateTime.UtcNow < deadline)
			{
				gone = await session.ReadXamlTreeAsync(cancellationToken);
				if (!(gone.Detail?.Contains("The target is stopped", StringComparison.Ordinal) ?? false)) break;
				await Task.Delay(200, cancellationToken);
			}

			Assert.DoesNotContain("The target is stopped", gone.Detail ?? string.Empty, StringComparison.Ordinal);
			Assert.DoesNotContain("Resume the target", gone.Detail ?? string.Empty, StringComparison.Ordinal);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Two bindings in one method each fire as themselves. A tracepoint on the method and a stopping
	/// breakpoint at a position inside it are both function breakpoints on the same metadata token, so
	/// only the IL offset tells them apart -- and a hit given to the wrong one turns a stop into a log
	/// line under another binding's id, reported as success.
	/// <para>
	/// The breakpoint goes at a position the source listing reported rather than at a hand-written
	/// offset, because that pairing is the one the listing invites a caller into: it offers every
	/// position in a method somebody has probably already set a breakpoint on by name.
	/// </para>
	/// <para>
	/// The tracepoint is registered first, so a match that cannot tell them apart attributes both hits
	/// to it and the target is never held at all.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_tracepoint_and_a_breakpoint_in_one_method_each_fire_as_itself()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			// Past the method's first instruction, so the two bindings differ by offset and the test is
			// about attribution rather than about one binding.
			var source = await session.ReadMethodSourceAsync("DebugProbeTarget.Program.Beat", cancellationToken);
			var inside = source.Positions.FirstOrDefault(position => position.IlOffset > 0);
			Assert.NotNull(inside);

			var tracepoint = await session.AddTracepointAsync(
				"DebugProbeTarget.Program.Beat", "beat traced", logEveryNthHit: null, condition: null, cancellationToken);
			Assert.True(tracepoint.Bound, $"tracepoint should bind; detail: {tracepoint.Detail}");

			// Generous, so the assertions below are not racing the safety timer for the stop.
			var breakpoint = await session.SetBreakpointAsync(
				inside!.Location, autoContinueSeconds: 60, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind at {inside.Location}; detail: {breakpoint.Detail}");

			var held = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(held);

			// The stop belongs to the breakpoint, by id rather than by the sentence it carries.
			var frames = await session.ReadFramesAsync(null, 0, null, cancellationToken);
			Assert.Equal(LiveExecutionState.StoppedAtBreakpoint, frames.Execution);
			Assert.Equal(breakpoint.Id, frames.Stop!.BreakpointId);

			// And each counted its own hits: the tracepoint logged the entry it is on, the breakpoint
			// took the one inside.
			var traced = await session.ListTracepointsAsync(cancellationToken);
			Assert.True(
				Assert.Single(traced.Tracepoints).HitCount > 0,
				"the tracepoint should have counted the hit at the method's entry");

			var stopping = await session.ListBreakpointsAsync(cancellationToken);
			Assert.True(
				Assert.Single(stopping.Breakpoints).HitCount > 0,
				"the breakpoint should have counted the hit at its own offset");

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "a stop holds the target rather than killing it");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}
}
