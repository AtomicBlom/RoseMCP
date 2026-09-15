using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;


using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The live-app debug session against an ordinary .NET child process: attaching, launching,
/// supervision, breakpoints, evaluation and stepping. Like the broker tests, these spawn a real host
/// process, because attach, supervision and reclaiming are properties of process lifetime -- and the
/// host attaches a real ICorDebug session to a real .NET process.
/// <para>
/// No fixture and no lease, which is why these are separate from the three probe-app suites. The
/// target is a console process this test starts and kills, so nothing here contends with anything:
/// they run in parallel with each other and with the rest of the suite. A probe app is
/// single-instance and can only be driven by one test at a time, and mixing the two kinds in one
/// class made that gate look like a property of the class rather than of the app.
/// </para>
/// </summary>
[Category("LiveApp")]
public sealed class LiveAppSessionTests
{
	/// <summary>
	/// The first dogfood, end to end: the broker launches a host in the target's architecture, the
	/// host attaches ICorDebug to a running .NET process, an exception thrown in that process is
	/// captured and readable, and closing the session detaches and reclaims the host while leaving the
	/// target running.
	/// <para>
	/// The target is a dedicated child process rather than this test runner: attaching a debugger to
	/// the process that is also running the test is unnecessary and perturbs it, whereas the child does
	/// nothing but throw a distinctively named exception on a loop for the debugger to catch.
	/// </para>
	/// </summary>
	[Test]
	public async Task Attaches_to_a_dotnet_process_and_captures_an_exception()
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

			var summary = session.Describe();
			Assert.Equal(LiveAppSessionState.Ready, summary.State);
			Assert.Equal(ExpectedArchitecture, summary.Architecture);
			Assert.Equal(child.Id, summary.TargetProcessId);
			Assert.NotNull(summary.HostProcessId);
			Assert.Single(manager.Describe());

			var marker = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseDebugProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(marker);
			Assert.Contains("RoseDebugProbeException", marker!.ExceptionType);

			// The exception carries a stack, and the throwing method is on it (#7, stack walk).
			Assert.NotNull(marker.Frames);
			Assert.Contains(marker.Frames!, frame => frame.Contains("DebugProbeTarget.Program"));

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.Empty(manager.Sessions);

			// Detach leaves the target running; the debugger did not take it down.
			Assert.False(child.HasExited, "the target survives being attached to and throwing");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A live-app session belongs to the MCP session that started it. The broker is a singleton every
	/// connection shares, so without this any client of an http broker reaches another client's
	/// debugger -- reading its captured exceptions and log output, setting breakpoints in its target,
	/// evaluating expressions inside it, detaching it -- by guessing an eight-character id.
	/// <para>
	/// Refused as though it were not there, because which of "no such session" and "not yours" it is
	/// is not the caller's business and the next step is the same either way. The list is scoped for
	/// the same reason: the refusal sends the caller to rose_debug_list, so a list naming sessions it
	/// cannot then use would be worse than no list.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_session_belongs_to_the_client_that_started_it()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			LiveAppSession session;

			using (CallSession.Use("mcp-session-one"))
			{
				session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
				Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

				Assert.NotNull(manager.Find(session.SessionId));
				Assert.Contains(manager.DescribeOwned(), row => row.SessionId == session.SessionId);
			}

			using (CallSession.Use("mcp-session-two"))
			{
				Assert.Null(manager.Find(session.SessionId));
				Assert.Empty(manager.DescribeOwned());

				// The loudest thing one client can do to another, and it goes through the same check.
				Assert.False(await manager.CloseAsync(session.SessionId, cancellationToken));
			}

			// The tray window and GET /admin/sessions read the whole picture, and still do: their reader
			// is the person running the broker rather than one of its clients.
			Assert.Contains(manager.Describe(), row => row.SessionId == session.SessionId);

			using (CallSession.Use("mcp-session-one"))
			{
				Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			}
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Being told about an event without polling for it (#8). The agent is turn-based, so a pushed MCP
	/// notification reaches nobody -- there is no listener between its turns. What it can use is one
	/// call that does not come back until there is something to say, which is what waitSeconds is.
	/// <para>
	/// The cursor is taken past everything already buffered first, so this genuinely waits rather than
	/// finding the answer sitting there. That the wait is woken by the event and not by a tick is the
	/// assertion worth making, so the elapsed time is checked against the timeout: returning at the
	/// deadline with the right content would pass a content-only assertion while being the behaviour
	/// this exists to avoid.
	/// </para>
	/// </summary>
	[Test]
	public async Task Waiting_for_an_event_answers_when_it_arrives_rather_than_on_a_timeout()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			// Everything so far, so the wait below has nothing already buffered to satisfy it.
			var caughtUp = await session.ReadEventsAsync(0, cancellationToken);

			var clock = System.Diagnostics.Stopwatch.StartNew();
			var waited = await session.ReadEventsAsync(
				caughtUp.NextCursor, [nameof(LiveDebugEventKind.ExceptionFirstChance)], 50, 30, cancellationToken);
			clock.Stop();

			Assert.NotEmpty(waited.Events);
			Assert.All(waited.Events, entry => Assert.Equal(LiveDebugEventKind.ExceptionFirstChance, entry.Kind));
			Assert.Contains(waited.Events, entry => entry.ExceptionType?.Contains("RoseDebugProbeException") ?? false);

			// The probe throws twice a second, so an answer that took anything like the full thirty
			// seconds came from the deadline rather than from the event.
			Assert.True(
				clock.Elapsed < TimeSpan.FromSeconds(20),
				$"the wait should have been woken by the event, not the timeout; it took {clock.Elapsed}");

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// The other half of the same contract: a wait for something that does not happen comes back
	/// empty when its time is up, rather than hanging or reporting events of some other kind. Waiting
	/// on BreakpointHit with no breakpoint set is exactly "wait for the next stop" against a target
	/// that never stops.
	/// </summary>
	[Test]
	public async Task Waiting_for_an_event_that_does_not_happen_comes_back_empty()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			var caughtUp = await session.ReadEventsAsync(0, cancellationToken);

			var clock = System.Diagnostics.Stopwatch.StartNew();
			var waited = await session.ReadEventsAsync(
				caughtUp.NextCursor, [nameof(LiveDebugEventKind.BreakpointHit)], 50, 2, cancellationToken);
			clock.Stop();

			Assert.Empty(waited.Events);
			Assert.Equal(LiveAppSessionState.Ready, waited.State);

			// It waited rather than answering at once, which is what makes the empty answer meaningful.
			Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.5), $"expected it to wait; it took {clock.Elapsed}");

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// #53: a detach that fails used to be swallowed, and the cleanup that followed it terminated the
	/// debugging interface while still attached -- which takes the debuggee down with it. The target
	/// surviving is the outcome; <see cref="LiveAppSession.DetachFailure"/> is the evidence, and it is
	/// the more useful assertion of the two, because a close that reports a reason is a bug report
	/// while a dead child process is a mystery.
	/// </summary>
	[Test]
	public async Task Closing_a_session_detaches_cleanly_and_leaves_the_target_running()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));

			Assert.Null(session.DetachFailure);
			Assert.False(child.HasExited, "detaching leaves the target running");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A target that has already exited has nothing to detach from, so closing must still report a
	/// clean detach. Worth its own test because the fix turned "detach" from something that returned
	/// nothing into something whose answer is now acted on: getting this case wrong would make every
	/// close after a target exits report a detach failure and fail rose_debug_detach outright.
	/// </summary>
	[Test]
	public async Task Closing_a_session_whose_target_has_already_exited_is_not_a_detach_failure()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);

		// Waited for, not just requested: the point is that the host observes the exit before the
		// close, which is what puts the detach down its already-gone path rather than its normal one.
		child.Kill(entireProcessTree: true);
		await child.WaitForExitAsync(cancellationToken);
		await WaitForEventAsync(session, entry => entry.Kind == LiveDebugEventKind.ProcessExited, cancellationToken);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));

		Assert.Null(session.DetachFailure);
	}

	/// <summary>
	/// #219: ICorDebug refuses to detach while any breakpoint the session bound is still active, so a
	/// session that did the one thing a debug session is for could not let go of the user's process --
	/// and what it left behind said nothing, since the session is gone from the list while the debugger
	/// is still on the target. The existing detach tests all detach from a session that never bound
	/// anything, which is why the suite never saw it; this one sets a breakpoint, waits for the hit, and
	/// closes with it still bound and the target still held.
	/// </summary>
	[Test]
	public async Task Closing_a_session_that_still_holds_a_breakpoint_detaches_cleanly()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var breakpoint = await session.SetBreakpointAsync("DebugProbeTarget.Program.Beat", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind against the loaded module; detail: {breakpoint.Detail}");

			// Waited for rather than merely set: an unbound breakpoint is not what ICorDebug objects to,
			// so a close raced ahead of the bind would pass while proving nothing.
			await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);

			// Neither removed nor continued: the breakpoint is bound and the target is held, which is
			// exactly the state the detach used to refuse.
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));

			Assert.Null(session.DetachFailure);
			Assert.False(child.HasExited, "detaching past a bound breakpoint leaves the target running");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

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

	private static LiveAppTarget AttachTo(int processId) => new()
	{
		Kind = LiveAppTargetKind.AttachProcess,
		ProcessId = processId,
		Description = "probe target",
	};

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
	/// Launching a process under the debugger (issue #4): the target is under debug from birth, so its
	/// earliest events -- the process-created notice and the first exceptions -- are captured, which
	/// attaching after the fact cannot see. Detaching leaves the launched process running.
	/// </summary>
	[Test]
	public async Task Launches_a_dotnet_process_under_the_debugger_and_captures_startup()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = ProbeTargetPath(),
			Description = "launched probe",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		int? launchedPid = null;
		try
		{
			var summary = session.Describe();
			Assert.Equal(LiveAppSessionState.Ready, summary.State);
			Assert.Equal(ExpectedArchitecture, summary.Architecture);
			Assert.NotNull(summary.TargetProcessId);
			launchedPid = summary.TargetProcessId;

			var created = await WaitForEventAsync(session, entry => entry.Kind == LiveDebugEventKind.ProcessCreated, cancellationToken);
			Assert.NotNull(created);

			var marker = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseDebugProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(marker);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (launchedPid is { } pid)
			{
				try
				{
					using var process = Process.GetProcessById(pid);
					if (!process.HasExited) process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
					// Already gone; nothing to reclaim.
				}
			}
		}
	}

	/// <summary>
	/// A live-app host dies with the client that spawned it, and takes a target it launched with it.
	/// </summary>
	/// <remarks>
	/// The half that already held is the exit: closing the host's stdin ends it, in about fifty
	/// milliseconds, the way it ends a worker. The half that did not is the target. Killing a wedged
	/// test host left both hosts and both probe apps running, and the probe app is single-instance --
	/// so the orphan is not a process that costs memory, it is a process the next run's fixture finds
	/// and mistakes for its own.
	/// <para>
	/// Driven against the host binary over a hand-written handshake rather than through
	/// <see cref="LiveAppSessionManager"/> or an <c>McpClient</c>, and both halves of that are
	/// load-bearing. The manager always detaches before it closes stdin, and a detach is precisely
	/// the request this must honour. And disposing an <c>McpClient</c> kills the child's whole
	/// process tree, which the target is in -- so a test written that way passes against the fix and
	/// against its absence, which is what the first draft of this did.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_live_app_host_takes_the_target_it_launched_when_its_client_goes_away()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var start = new ProcessStartInfo(LiveAppHostLauncher.ResolveHostPath(ExpectedArchitecture, new BrokerOptions()))
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in new[] { "--launch", ProbeTargetPath(), "--description", "orphan probe" })
		{
			start.ArgumentList.Add(argument);
		}

		using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the live-app host.");

		// Drained, not ignored: the host logs to stderr, and a full pipe buffer stops the process
		// this test is waiting on.
		var draining = host.StandardError.ReadToEndAsync(cancellationToken);

		int targetProcessId;
		try
		{
			await SendAsync(host, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rose-tests","version":"1"}}}""");
			await ReadReplyAsync(host, cancellationToken);

			await SendAsync(host, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

			// The tool name substituted rather than interpolated: a raw literal ending in three braces
			// cannot also carry an interpolation, and spelling the name again is how a constant stops
			// being one.
			await SendAsync(
				host,
				"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"TOOL","arguments":{}}}"""
					.Replace("TOOL", ToolNames.LiveAppInfo, StringComparison.Ordinal));

			using var reply = JsonDocument.Parse(await ReadReplyAsync(host, cancellationToken));
			var info = reply.RootElement.GetProperty("result").GetProperty("structuredContent");

			Assert.Equal(nameof(LiveAppSessionState.Ready), info.GetProperty("state").GetString());

			targetProcessId = info.GetProperty("targetProcessId").GetInt32();
		}
		catch
		{
			if (!host.HasExited) host.Kill(entireProcessTree: true);
			throw;
		}

		// The client going away, and nothing else: stdin closes, no detach was asked for, and nobody
		// reaches into the process tree.
		host.StandardInput.Close();

		await AssertGoneAsync(host.Id, "the host", cancellationToken);
		await AssertGoneAsync(targetProcessId, "the target it launched", cancellationToken);

		await draining;
	}

	/// <summary>One JSON-RPC frame to a host driven directly, which is newline-delimited and nothing else.</summary>
	private static async Task SendAsync(Process host, string frame)
	{
		await host.StandardInput.WriteLineAsync(frame);
		await host.StandardInput.FlushAsync();
	}

	/// <summary>
	/// The next line of the host's stdout, or a failure saying it never came. Bounded, because the
	/// alternative to a bound here is a test that hangs a run -- which is the family of defect this
	/// block of work is about.
	/// </summary>
	private static async Task<string> ReadReplyAsync(Process host, CancellationToken cancellationToken)
	{
		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		bounded.CancelAfter(TimeSpan.FromSeconds(60));

		try
		{
			return await host.StandardOutput.ReadLineAsync(bounded.Token)
				?? throw new InvalidOperationException("The live-app host closed its stdout without replying.");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new InvalidOperationException("The live-app host did not reply within 60s.");
		}
	}

	/// <summary>
	/// Waits for a process to be gone, and fails naming which one it was still waiting for. Bounded
	/// rather than immediate because exiting is not instantaneous, and generously rather than tightly
	/// because the number is not what is under test.
	/// </summary>
	private static async Task AssertGoneAsync(int processId, string what, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

		while (DateTime.UtcNow < deadline)
		{
			if (!IsRunning(processId)) return;

			await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		}

		Assert.Fail($"{what} (pid {processId}) was still running 30s after the client went away.");
	}

	private static bool IsRunning(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	/// <summary>
	/// The architecture shim (issue #1): a target running as a different architecture than the broker
	/// is attached to by a host launched to match it. On this Windows-on-ARM box the target is x64 under
	/// emulation while the broker is ARM64, which is the exact case classic UWP needs; on an x64 machine
	/// it is a same-architecture x64 attach. Skips where the x64 .NET runtime is unavailable.
	/// </summary>
	[Test]
	public async Task Attaches_to_a_target_of_a_different_architecture()
	{
		EnsureX64HostBuilt();
		var x64Target = EnsureX64ProbeTargetBuilt();

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProcess(x64Target);
		try
		{
			await Task.Delay(1500, cancellationToken);
			if (child.HasExited)
			{
				Skip.Test($"The x64 probe target exited (code {child.ExitCode}); the x64 .NET runtime is not available here.");
			}

			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "x64 probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			var summary = session.Describe();
			Assert.True(
				summary.State == LiveAppSessionState.Ready,
				$"expected Ready, got {summary.State}: {summary.Detail} (host arch {summary.Architecture}, host pid {summary.HostProcessId})");
			Assert.Equal(TargetArchitecture.X64, summary.Architecture);

			var marker = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseDebugProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(marker);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target survives an attach across architectures");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// The same shim on x86, which shipped a host and nothing that used one.
	/// </summary>
	/// <remarks>
	/// An install carries an x86 host on every machine, ARM64 and x64 alike, and until this nothing
	/// built an x86 target or attached to one -- so the claim that an x86 target debugs rested on the
	/// host merely being published. x86 is not a legacy case here either: it is the default platform
	/// of the modern UWP project template, so it is what an ordinary new app is built as.
	/// <para>
	/// A plain console target rather than the UWP probe, deliberately. What is unproven is ICorDebug
	/// through an x86 host, and a packaged app adds registration, activation and an AppContainer to a
	/// question that is about none of them.
	/// </para>
	/// </remarks>
	[Test]
	public async Task Attaches_to_an_x86_target()
	{
		EnsureX86HostBuilt();
		var x86Target = EnsureX86ProbeTargetBuilt();

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProcess(x86Target);

		try
		{
			await Task.Delay(1500, cancellationToken);

			if (child.HasExited)
			{
				Skip.Test($"The x86 probe target exited (code {child.ExitCode}); the x86 .NET runtime is not available here.");
			}

			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "x86 probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			var summary = session.Describe();

			Assert.True(
				summary.State == LiveAppSessionState.Ready,
				$"expected Ready, got {summary.State}: {summary.Detail} (host arch {summary.Architecture}, host pid {summary.HostProcessId})");

			Assert.Equal(TargetArchitecture.X86, summary.Architecture);

			// Attaching is not the claim; debugging is. The target throws on a cycle, so a first-chance
			// exception arriving through the x86 host is the whole of what was unproven.
			var marker = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseDebugProbeException") ?? false),
				cancellationToken);

			Assert.NotNull(marker);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the x86 target survives the attach");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>A target that has already gone is reported faulted, not thrown.</summary>
	[Test]
	public async Task Reports_a_missing_target_as_faulted()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// A process id that is essentially certain not to exist.
		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.AttachProcess,
			ProcessId = 0x7FFF_FFF0,
			Description = "missing target",
		};

		var session = await manager.StartAsync(target, cancellationToken);

		Assert.Equal(LiveAppSessionState.Faulted, session.Describe().State);
	}

	/// <summary>
	/// A XAML failure carries the target's own heartbeat, which is what separates a target that is not
	/// answering from a target that is not running.
	/// </summary>
	/// <remarks>
	/// The two are indistinguishable from outside the process and easy to confuse for a long time. A
	/// UWP app frozen by PLM -- which is what happens to a backgrounded app whose package has no debug
	/// mode, and a detach removes debug mode -- stops all its threads through a job object without
	/// marking any of them suspended, so its CPU time, its thread states and its unresponsive window
	/// all look exactly like an app that is running and wedged. The debug events stopping is the one
	/// difference, so their age is reported beside every XAML failure.
	/// <para>
	/// An ordinary .NET target is the honest way to test it: it has no XAML at all, so the endpoint
	/// genuinely never appears, and it throws on a loop, so it is demonstrably executing while we wait.
	/// That is the combination the message has to describe correctly -- not answering, but alive.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_xaml_failure_reports_how_long_ago_the_target_last_ran()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "probe target",
				},
				cancellationToken);

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			// Let the target throw at least once, so there is a heartbeat to report.
			var beat = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance,
				cancellationToken);
			Assert.NotNull(beat);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.Empty(tree.Nodes);
			Assert.NotNull(tree.Detail);
			Assert.Contains("last debug event", tree.Detail!);

			// The number, not just the sentence. An event inside the endpoint's own budget proves the
			// target was executing throughout the wait that just failed -- which is the whole distinction
			// this exists to draw, and the bound is that budget rather than a figure picked here.
			var match = System.Text.RegularExpressions.Regex.Match(tree.Detail!, @"was (\d+(?:\.\d+)?)s ago");
			Assert.True(match.Success, $"the detail should quote the heartbeat's age; got: {tree.Detail}");

			var age = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
			Assert.InRange(age, 0, 20);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
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

	private static TargetArchitecture ExpectedArchitecture => RuntimeInformation.ProcessArchitecture switch
	{
		System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
		System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.Arm64,
		System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
		_ => TargetArchitecture.Unknown,
	};
}
