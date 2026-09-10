using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Integration tests for the live-app debug session. Like the broker tests, these spawn a real host
/// process, because attach, supervision, and reclaiming are properties of process lifetime -- and the
/// host attaches a real ICorDebug session to a real .NET process.
/// <para>
/// Twenty of these tests drive one registered UWP app, and a packaged app is single-instance: a
/// second launch by AUMID activates the instance that exists rather than starting one under the
/// debugger, so two of them running together would have one attach to nothing. They take a lease
/// from <see cref="UwpProbeApp"/> for exactly that reason, and the eleven tests here that debug an
/// ordinary .NET child process take none -- they are independent and run in parallel with the rest
/// of the suite. Serialising the whole class instead is the obvious move and costs 109 seconds:
/// it stops the class overlapping <em>anything</em>, not just itself.
/// </para>
/// </summary>
/// <remarks>
/// Each probe is shared for the whole assembly and each gets a source of its own rather than one
/// shared gate. They drive different processes and share no package, provider or window, so there is
/// nothing to serialise between them -- only within each. Sharing one would serialise three suites
/// that never contend.
/// </remarks>
[Category("LiveApp")]
[ClassDataSource<UwpProbeApp, WinUiProbeApp, UwpModernProbeApp>(
	Shared = [SharedType.PerAssembly, SharedType.PerAssembly, SharedType.PerAssembly])]
public sealed class LiveAppSessionTests(UwpProbeApp probe, WinUiProbeApp winui, UwpModernProbeApp uwpModern)
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

	/// <summary>
	/// The UWP path end to end (#4 UWP): build the classic UWP probe app, register it, and have the
	/// broker put it in debug mode, activate it, and attach -- through the x64 host, since classic UWP
	/// runs x64 emulated on ARM64 -- then capture the exception its Tick throws. Skips where the UWP
	/// build toolchain or app registration is not available, so the suite stays green without them.
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Launches_and_debugs_the_classic_uwp_probe_app()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: false, TestContext.Current!.Execution.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			var summary = session.Describe();
			Assert.True(
				summary.State == LiveAppSessionState.Ready,
				$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");
			Assert.Equal(TargetArchitecture.X64, summary.Architecture);

			var marker = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(marker);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// Startup capture from birth (#5): the same UWP path, but proving the debugger is present before the
	/// app's first managed instruction. The probe throws a one-time RoseUwpStartupException inside its
	/// OnLaunched, before the window shows; an attach that lands a beat after activation would have missed
	/// it, so catching it proves the resume stub attached from the runtime's first breath.
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Captures_the_classic_uwp_probe_apps_startup_from_birth()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: false, TestContext.Current!.Execution.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp startup probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			var summary = session.Describe();
			Assert.True(
				summary.State == LiveAppSessionState.Ready,
				$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

			// The startup exception fires inside OnLaunched, before the timer's first tick; only a
			// from-birth attach is present in time to see it.
			var startup = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpStartupException") ?? false),
				cancellationToken);
			Assert.NotNull(startup);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// The WinUI 3 path, unpackaged (#106). An unpackaged WinUI 3 app is an ordinary desktop process
	/// with no package identity and no AppContainer, which is the shape the live-app half kept getting
	/// wrong, so it is the one worth proving first.
	/// <para>
	/// The debugger needs no WinUI-specific code (#79) -- this is here to keep that true rather than
	/// to establish it, and to give the seams work (#75) something to run against.
	/// </para>
	/// </summary>
	[Test]
	[WinUiProbe]
	public async Task Launches_and_debugs_the_unpackaged_winui_probe_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: false, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = turn.ExecutablePath,
			Description = "winui probe (unpackaged)",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		var marker = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseWinUiProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(marker);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The same app, packaged. Worth its own test because packaged and unpackaged are different
	/// targets rather than two ways of shipping one: this one has package identity and is activated
	/// by AUMID rather than launched by path.
	/// <para>
	/// It is still not in an AppContainer, which is the thing measuring this settled. A packaged WinUI
	/// 3 app is a packaged *desktop* app -- runFullTrust, Windows.FullTrustApplication -- so packaging
	/// and sandboxing come apart here in a way they never do for classic UWP, and only the UWP tap
	/// needs the work folder granted to ALL APPLICATION PACKAGES.
	/// </para>
	/// </summary>
	[Test]
	[WinUiProbe]
	public async Task Launches_and_debugs_the_packaged_winui_probe_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: true, needsXamlProvider: false, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = turn.Aumid,
			Description = "winui probe (packaged)",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		var marker = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseWinUiProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(marker);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The XAML tree of a WinUI 3 target this session started (#76).
	/// </summary>
	/// <remarks>
	/// <para>
	/// This was a refusal test until the cause was found, and its inversion is the signal that #76 is
	/// done -- which is what the refusal's own comment said would happen.
	/// </para>
	/// <para>
	/// What it proves past "a tree came back" is that the shared tap serves a second framework
	/// unchanged: the same walk, the same snapshot and the same named elements as the UWP probe, out
	/// of Microsoft.UI.Xaml. The one thing WinUI 3 needed was that the walk not be advised from the
	/// UI thread, and that lives in the provider seam rather than here.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_the_xaml_tree_of_a_winui_app_it_launched()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = turn.ExecutablePath,
			Description = "winui xaml probe",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		// Well into running, so an empty tree cannot be an app that has not built one yet.
		var running = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseWinUiProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(running);

		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
		Assert.NotEmpty(tree.Nodes);

		// The same names the UWP probe declares, because the two apps mirror each other on purpose.
		foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
		{
			Assert.Contains(tree.Nodes, node => node.Name == name);
		}

		// Rooting works the same here: a named element's subtree carries its descendants and not its
		// parent. Asserted on WinUI too because the address grammar is computed from the live tree,
		// and the live tree is the half that differs between the frameworks.
		var panelSubtree = await session.ReadXamlTreeAsync("Panel", offset: 0, limit: 0, cancellationToken);
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Panel");
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Caption");
		Assert.DoesNotContain(panelSubtree.Nodes, node => node.Name == "RootGrid");

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The XAML tree of a WinUI 3 app this session attached to rather than started (#76).
	/// </summary>
	/// <remarks>
	/// The companion to the launched case, and the one that settles the premise both refusals rested
	/// on. WinUI 3 was believed to need diagnostics enabled from startup, so that attaching could
	/// never work; it does work, because that belief was inferred from a failure whose real cause was
	/// a deadlock of our own making. Attaching is also the case an agent actually meets -- the app is
	/// already running by the time anyone asks about it -- so it is worth its own test rather than
	/// being assumed to follow from the launched one.
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_the_xaml_tree_of_a_winui_app_it_attached_to()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		// Started outside the session on purpose: nothing about this process was arranged for us.
		using var child = StartProcess(turn.ExecutablePath);
		await using var manager = CreateManager();

		try
		{
			// Waits for the window rather than for a fixed six seconds, so a probe that died at startup
			// says so instead of being attached to (#129).
			await WaitForProbeWindowAsync(child, cancellationToken);

			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "winui probe (attached)",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
			Assert.NotEmpty(tree.Nodes);

			foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
			{
				Assert.Contains(tree.Nodes, node => node.Name == name);
			}

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A second session over an app a first session has already inspected and let go of.
	/// </summary>
	/// <remarks>
	/// The scenario an agent meets whenever it comes back to an app it looked at earlier, and nothing
	/// covered it. The provider cannot be unloaded, so the second attach meets a target that already
	/// has one loaded, with the first session's channel torn down under it.
	/// <para>
	/// What this does <em>not</em> prove is that the provider's pipe reader can be restarted, which is
	/// the repair the C++ side of this change makes. Each host shadow-copies its own provider, so the
	/// second session loads a separate module with its own globals and never reaches the path where a
	/// reader has already stopped; the test passes with that repair and without it, which was
	/// established by reverting it and running this again rather than assumed. Reaching it would mean
	/// dropping the pipe under a live session, and the pipe belongs to the host process.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_the_xaml_tree_again_after_the_first_session_closed_the_pipe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "winui probe (attached twice)",
			};

			// Each manager owns its own pipe, so disposing the first is what takes the channel away
			// under a provider that goes on running.
			var firstLogs = new RecordingLoggerFactory();

			await using (var first = CreateManager(firstLogs))
			{
				var session = await first.StartAsync(target, cancellationToken);
				var tree = await session.ReadXamlTreeAsync(cancellationToken);

				Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
				Assert.Contains(tree.Nodes, node => node.Name == "RootGrid");
				Assert.Contains(firstLogs.Lines, line => line.Contains("provider connected on", StringComparison.Ordinal));
				Assert.True(await first.CloseAsync(session.SessionId, cancellationToken));
			}

			var secondLogs = new RecordingLoggerFactory();

			await using var second = CreateManager(secondLogs);

			var again = await second.StartAsync(target, cancellationToken);
			var reread = await again.ReadXamlTreeAsync(cancellationToken);

			Assert.True(reread.Detail is null, $"expected a tree on the second session, got detail: {reread.Detail}");
			Assert.Contains(reread.Nodes, node => node.Name == "RootGrid");

			// The provider connects for the second session as well, rather than the session falling
			// back to the work folder without saying so.
			Assert.Contains(secondLogs.Lines, line => line.Contains("provider connected on", StringComparison.Ordinal));

			Assert.True(await second.CloseAsync(again.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Every wait on the provider channel is bounded, and the sentence that comes back names which
	/// channel ran out and how long it was given.
	/// </summary>
	/// <remarks>
	/// The bound this drives is the injection call itself, which had none. It is a blocking
	/// cross-process call served by the target's UI thread, so a target wedged below managed code
	/// never returns from it -- which is how a full suite run hung for fifty minutes on a first tree
	/// read, the pipe logged as listening and no line after it.
	/// <para>
	/// Driven by shortening the bound rather than by wedging an app, because a wedged UI thread is not
	/// something a test can arrange on demand and a test that waits for a real hang is the very thing
	/// this is fixing. A millisecond is far below what loading a DLL into another process and walking
	/// its tree can take, so the bound expires every time; the abandoned injection completes into the
	/// work folder afterwards, harmlessly, which is why this takes the app for itself.
	/// <para>
	/// What it does not prove is that the abandoned call was genuinely blocked rather than merely
	/// slow. Nothing here can prove that: the wait is bounded either way, and the difference is
	/// invisible from this side by construction.
	/// </para>
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Bounds_the_wait_on_the_xaml_injection_call_and_names_the_channel()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			// Process-wide, and safe because the host reads it at startup and this turn holds the only
			// gate under which a live-app host is started. Zero rather than a small number: a bound of one
			// millisecond is really a wait of fifteen, because that is the scheduler's granularity, and an
			// injection into a warm app finishes inside that often enough to pass at random.
			using var shortened = new EnvironmentVariable("ROSEMCP_XAML_TIMEOUT_SECONDS", "0");

			await using var manager = CreateManager();

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "winui probe (bounded injection)",
				},
				cancellationToken);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.NotNull(tree.Detail);
			Assert.Contains("the XAML diagnostics injection call", tree.Detail, StringComparison.Ordinal);
			Assert.Contains("timed out after", tree.Detail, StringComparison.Ordinal);
			Assert.Empty(tree.Nodes);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// The provider pipe serves the reads it says it does: the first read of a session injects and
	/// answers through the work folder, and every read after it is a message to the resident reader.
	/// </summary>
	/// <remarks>
	/// The pipe connected, greeted, and then served nothing, unchanged for two releases -- invisible
	/// because both channels return the same tree, so every test that asserted the tree passed
	/// either way. The result names its channel now, which is the only thing that makes this
	/// assertable at all.
	/// <para>
	/// The first read cannot use the pipe and that is by construction rather than a shortcoming: the
	/// provider is not in the app until something injects it, and the pipe name travels in that
	/// injection's initialisation data. So the first read is asserted as the work folder, which also
	/// keeps the second assertion honest -- a session that reported "pipe" for both would mean the
	/// field was not being read off the path actually taken.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task The_second_xaml_read_of_a_session_is_served_over_the_pipe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			await using var manager = CreateManager();

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "winui probe (channel)",
				},
				cancellationToken);

			// The first read injects, because that is what loads the provider, and is then answered on the
			// pipe like every other request. Injection carries no request of its own, so a read that came
			// back from the work folder would mean the provider had not connected.
			var first = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.True(first.Detail is null, $"expected a tree, got detail: {first.Detail}");
			Assert.Equal("pipe", first.Channel);

			var second = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.True(second.Detail is null, $"expected a tree, got detail: {second.Detail}");
			Assert.Equal("pipe", second.Channel);

			// The same tree either way, which is what made the pipe's silence invisible.
			Assert.Contains(second.Nodes, node => node.Name == "RootGrid");
			Assert.Equal(first.Count, second.Count);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// The pipe serves a classic UWP target too, which is the case that can genuinely fail: the
	/// provider runs inside an AppContainer and reaches the pipe only through the two SIDs the host
	/// grants on it.
	/// </summary>
	/// <remarks>
	/// The WinUI probe cannot answer this. Unpackaged WinUI 3 is in nobody's AppContainer, so its
	/// end of the pipe is an ordinary CreateFile that would succeed with no grants at all -- which
	/// makes it the wrong target to conclude anything about the ACL from.
	/// <para>
	/// Two reads, and only the second is asserted. The shared app is shared, so whether this
	/// session's first read has already happened is not something one test gets to know; reading
	/// twice makes the second a second read either way.
	/// </para>
	/// </remarks>
	[Test]
	[ClassicSession]
	public async Task A_uwp_xaml_read_reaches_the_pipe_from_inside_the_app_container()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var turn = await probe.TakeSessionAsync(cancellationToken);

		await turn.Session.ReadXamlTreeAsync(cancellationToken);

		var second = await turn.Session.ReadXamlTreeAsync(cancellationToken);

		Assert.True(second.Detail is null, $"expected a tree, got detail: {second.Detail}");
		Assert.Equal("pipe", second.Channel);
	}

	/// <summary>
	/// The tap gives its two framework interfaces back when the session detaches, and says so.
	/// </summary>
	/// <remarks>
	/// They were released only from <c>SetSite(nullptr)</c>, which nothing reaches -- no tap is ever
	/// unadvised -- so every injection left an <c>IXamlDiagnostics</c> and an
	/// <c>IVisualTreeService</c> held for the life of the app, and the app outlives the session on
	/// purpose. A destructor would not have helped: the framework's advise and the reader's active
	/// pointer both hold a reference, so the object is never deleted either.
	/// <para>
	/// Asserted on the host's line rather than the provider's log file, and that is what decided
	/// where the release goes. The provider writes into the work folder the host is about to delete,
	/// and a release done at host shutdown is written after the client has closed the stdin carrying
	/// it -- so the detach asks over the pipe and reports the answer, while there is still a channel
	/// to report on.
	/// </para>
	/// <para>
	/// On the classic UWP probe rather than the WinUI one, because the WinUI probe cannot be launched
	/// reliably on this machine: two of two full runs had it exit at startup with
	/// REGDB_E_CLASSNOTREG, and the helper that meets that skips. A skip is the one outcome an
	/// acceptance test must not have, since it reads as green. The tap is shared code, so which
	/// framework hosts it does not change what is under test here.
	/// </para>
	/// </remarks>
	[Test]
	[ClassicOwnApp]
	public async Task The_tap_releases_its_interfaces_when_the_session_detaches()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: true, cancellationToken);

		var logs = new RecordingLoggerFactory();
		await using var manager = CreateManager(logs);

		var session = await manager.StartAsync(
			new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = turn.Aumid,
				Description = "uwp probe (detach)",
			},
			cancellationToken);

		// The first tick is the signal that the tree is up, and there is no provider in the app --
		// so nothing holding anything -- until a read has injected one.
		await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
			cancellationToken);

		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));

		Assert.Contains(
			logs.Lines,
			line => line.Contains("released its diagnostics interfaces on detach", StringComparison.Ordinal));
	}

	/// <summary>
	/// Reading and editing properties on WinUI 3 (#115), which nothing covered.
	/// </summary>
	/// <remarks>
	/// The WinUI tests asserted the tree and stopped there, so every property path was exercised on
	/// UWP only -- and two places in the shared header spelled <c>Windows.UI.Xaml</c> as a literal.
	/// A CornerRadius is the one that shows: XAML diagnostics renders the struct as an empty string
	/// on both frameworks, and the rescue that reads it off the element compared the declared type
	/// against the UWP name, so on WinUI 3 it never fired and the property read back empty. Empty is
	/// indistinguishable from unset, which is why this went unnoticed: the answer looked like a
	/// framework quirk rather than a wrong comparison.
	/// <para>
	/// The apply half is here for the same reason. It is the seam the toolbar that never drew lived
	/// behind: everything inspectable said yes while the screen said no, because no WinUI provider
	/// was ever exercised past the tree.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_and_edits_properties_on_a_winui_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);
		await using var manager = CreateManager();

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "winui probe (properties)",
				},
				cancellationToken);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var pane = tree.Nodes.FirstOrDefault(node => node.Name == "Pane");
			var caption = tree.Nodes.FirstOrDefault(node => node.Name == "Caption");

			Assert.NotNull(pane);
			Assert.NotNull(caption);

			var properties = await session.ReadXamlPropertiesAsync(pane!.Handle, includeDefaults: false, cancellationToken);

			Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");

			// The markup sets CornerRadius="8" on Pane. The framework stringifies it as nothing, so a
			// value here is the rescue firing -- and the rescue only fires if it recognises the type
			// under its Microsoft.UI.Xaml name.
			var cornerRadius = properties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");

			Assert.NotNull(cornerRadius);
			Assert.False(
				string.IsNullOrEmpty(cornerRadius!.Value),
				"CornerRadius came back empty, which is the shared header comparing against the UWP type name.");

			// A property the framework does stringify, to show the empty one above is not simply how
			// this element reads.
			var padding = properties.Properties.FirstOrDefault(property => property.Name == "Padding");

			Assert.NotNull(padding);
			Assert.False(string.IsNullOrEmpty(padding!.Value), "Padding reports a value, which is what makes the empty one above a finding");

			// And the apply half: a property edit lands and reads back.
			var markup = Path.Combine(TestToolchain.RepositoryRoot(), "tests", "apps", "winui", "MainWindow.xaml");
			var before = await File.ReadAllTextAsync(markup, cancellationToken);
			var after = before.Replace("Text=\"Rose WinUI Probe\"", "Text=\"edited on winui\"", StringComparison.Ordinal);

			Assert.NotEqual(before, after);

			var edit = await session.ApplyXamlAsync(before, after, filePath: null, cancellationToken);

			Assert.True(edit.Detail is null, $"expected the edit to apply, got detail: {edit.Detail}");
			Assert.Equal(1, edit.Applied);
			Assert.All(edit.Results, result => Assert.Equal("applied", result.Status));

			var afterwards = await session.ReadXamlPropertiesAsync(caption!.Handle, includeDefaults: false, cancellationToken);
			var text = afterwards.Properties.FirstOrDefault(property => property.Name == "Text");

			Assert.NotNull(text);
			Assert.Equal("edited on winui", text!.Value);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A UWP app on modern .NET, launched from birth and debugged (#117).
	/// </summary>
	/// <remarks>
	/// The runtime half, on its own, because it is the half that actually differs. The app model and
	/// the XAML framework are the classic probe's; what is new underneath is CoreCLR reached through a
	/// CsWinRT projection, which puts every managed frame behind an ABI layer -- the startup exception
	/// arrives through <c>ABI.Windows.UI.Xaml.IApplicationOverrides.Do_Abi_OnLaunched_1</c> rather
	/// than a direct framework call. Nothing here had seen that shape before, so "the debugger still
	/// resolves our types through it" is worth asserting rather than assuming.
	/// <para>
	/// It also pins the thing the classic probe cannot: uwp-classic is debuggable only as Debug x64,
	/// because every other configuration forces .NET Native. This one is CoreCLR in every architecture
	/// it builds, and the fixture builds it for the host's.
	/// </para>
	/// </remarks>
	[Test]
	[ModernUwpProbe]
	public async Task Launches_and_debugs_the_modern_uwp_probe_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var turn = await uwpModern.TakeAsync(needsXamlProvider: false, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = turn.Aumid,
			Description = "modern uwp probe",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		// Thrown once in OnLaunched, before the window is shown, so only a debugger that was present
		// from the runtime's first breath sees it. An attach landing a beat after activation is already
		// too late, which is what makes this the from-birth assertion rather than merely a liveness one.
		var startup = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpModernStartupException") ?? false),
			cancellationToken);
		Assert.NotNull(startup);

		var ticking = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpModernProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(ticking);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The live XAML tree and one element's properties, on a UWP app running modern .NET (#117).
	/// </summary>
	/// <remarks>
	/// The claim under test is that the UWP tap serves this app unmodified -- same endpoint, same
	/// initialiser, same dispatcher seam, same AppContainer grants -- because the XAML framework is
	/// the same Windows.UI.Xaml regardless of which runtime is calling into it. That is a claim about
	/// something the tap cannot see, so it is the kind that holds right up until it does not.
	/// <para>
	/// Properties are read in the same test rather than a second one, deliberately. They need the app
	/// up, and bringing a packaged app up is six seconds; splitting them buys isolation this pair does
	/// not need, since reading a tree and reading a property of it are the same operation twice.
	/// </para>
	/// </remarks>
	[Test]
	[ModernUwpProbe]
	public async Task Reads_the_xaml_tree_of_a_modern_uwp_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var turn = await uwpModern.TakeAsync(needsXamlProvider: true, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = turn.Aumid,
			Description = "modern uwp xaml probe",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		// Well into running, so an empty tree cannot be an app that has not built one yet.
		var running = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpModernProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(running);

		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
		Assert.NotEmpty(tree.Nodes);

		// The same names the classic UWP probe declares, because the two apps mirror each other on
		// purpose -- the modern one is generated from the classic one's markup.
		foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
		{
			Assert.Contains(tree.Nodes, node => node.Name == name);
		}

		var panelSubtree = await session.ReadXamlTreeAsync("Panel", offset: 0, limit: 0, cancellationToken);
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Panel");
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Caption");
		Assert.DoesNotContain(panelSubtree.Nodes, node => node.Name == "RootGrid");

		// Source info survives the projection. It comes from the markup compiler rather than the
		// runtime, and a UseUwp project runs the same compiler, so it should -- but it is the one piece
		// of this that is generated at build time rather than read off the live tree.
		var pane = Assert.Single(tree.Nodes, node => node.Name == "Pane");
		var properties = await session.ReadXamlPropertiesAsync(pane.Handle, includeDefaults: false, cancellationToken);
		Assert.NotEmpty(properties.Properties);
		Assert.Contains(properties.Properties, property => property.Name == "CornerRadius");

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The XAML track's first vertical (#2/#3, seed of #9): launch the classic UWP probe, inject the
	/// diagnostics provider, and read its live visual tree. Proves the provider builds, injects into the
	/// AppContainer, enumerates on the UI thread, and reports the tree back through the host to the
	/// broker. Skips where the UWP build toolchain or the C++ toolset is absent.
	/// </summary>
	[Test]
	[ClassicSlot(0)]
	public async Task Reads_the_live_visual_tree_of_the_classic_uwp_probe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C, reading only. It asserts on the elements the markup declares, so it neither needs a
		// slot nor minds one being busy: what it looks for is furniture, and no phase C test edits
		// anything outside its own slot.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
			Assert.NotEmpty(tree.Nodes);

			// The probe's named elements are all present, addressable from the flat parent/child list.
			foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
			{
				Assert.Contains(tree.Nodes, node => node.Name == name);
			}

			// Rooting returns a named element's subtree only (#9): Panel's subtree has its descendants
			// (the caption) but not its parent (RootGrid).
			var panelSubtree = await session.ReadXamlTreeAsync("Panel", offset: 0, limit: 0, cancellationToken);
			Assert.Contains(panelSubtree.Nodes, node => node.Name == "Panel");
			Assert.Contains(panelSubtree.Nodes, node => node.Name == "Caption");
			Assert.DoesNotContain(panelSubtree.Nodes, node => node.Name == "RootGrid");

			// Paging: a limited page carries at most that many nodes, and Total says how many matched.
			var firstPage = await session.ReadXamlTreeAsync(root: null, offset: 0, limit: 2, cancellationToken);
			Assert.Equal(2, firstPage.Nodes.Count);
			Assert.True(firstPage.Total > 2, $"expected more than a page of nodes; total {firstPage.Total}");

			// The three spellings of one element all reach it. The address is the one that matters: it is
			// what an element with no x:Name has instead, which is everything inside a control template,
			// and passing it back was refused for not being a number.
			var caption = tree.Nodes.Single(node => node.Name == "Caption");

			Assert.NotNull(caption.Address);
			Assert.Equal(caption.Handle, await session.ResolveElementAsync(caption.Handle.ToString(), cancellationToken));
			Assert.Equal(caption.Handle, await session.ResolveElementAsync("#Caption", cancellationToken));
			Assert.Equal(caption.Handle, await session.ResolveElementAsync("Caption", cancellationToken));
			Assert.Equal(caption.Handle, await session.ResolveElementAsync(caption.Address!, cancellationToken));

			// And an address roots the tree, which the host itself cannot do -- it knows only names.
			var byAddress = await session.ReadXamlTreeAsync(caption.Address, offset: 0, limit: 0, cancellationToken);

			Assert.Contains(byAddress.Nodes, node => node.Handle == caption.Handle);

			var missing = await Assert.ThrowsAsync<ArgumentException>(
				() => session.ResolveElementAsync("#NotThere", cancellationToken));

			Assert.Contains("Nothing in the live tree is called", missing.Message, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// XAML property inspection (#10): read an element's properties with provenance. Confirms the set
	/// (non-default) properties come back with a Local provenance for what the XAML sets -- including a
	/// concrete string value -- that framework defaults are filtered out unless asked for, and that it
	/// all rides through the host to the broker. Skips where the UWP or C++ toolchain is absent.
	/// </summary>
	[Test]
	[ClassicSlot(1)]
	public async Task Reads_the_properties_of_a_xaml_element()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C, and one that reads the app's declared furniture rather than building its own. It
		// has to: half of what it asserts is source info -- which file and line declared the element --
		// and an element this test created at runtime has none, because nothing declared it. So the
		// slot goes unused here, and what makes this safe is the other half of the bargain: no phase C
		// test edits anything outside its own slot.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var rootGrid = tree.Nodes.FirstOrDefault(node => node.Name == "RootGrid");
			var caption = tree.Nodes.FirstOrDefault(node => node.Name == "Caption");
			Assert.NotNull(rootGrid);
			Assert.NotNull(caption);

			var properties = await session.ReadXamlPropertiesAsync(rootGrid!.Handle, includeDefaults: false, cancellationToken);
			Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");
			Assert.NotEmpty(properties.Properties);

			// Background is set in the probe's XAML, so it reads back with Local provenance...
			var background = properties.Properties.FirstOrDefault(property => property.Name == "Background");
			Assert.NotNull(background);
			Assert.Equal("Local", background!.Provenance);

			// ...and the framework defaults are filtered out unless asked for.
			Assert.DoesNotContain(properties.Properties, property => property.Provenance == "Default");

			var withDefaults = await session.ReadXamlPropertiesAsync(rootGrid.Handle, includeDefaults: true, cancellationToken);
			Assert.Contains(withDefaults.Properties, property => property.Provenance == "Default");
			Assert.True(withDefaults.Count > properties.Count);

			// A concrete string value comes through: the caption's Text is exactly what the XAML sets.
			var captionProperties = await session.ReadXamlPropertiesAsync(caption!.Handle, includeDefaults: false, cancellationToken);
			var text = captionProperties.Properties.FirstOrDefault(property => property.Name == "Text");
			Assert.NotNull(text);
			Assert.Equal("Rose UWP Probe", text!.Value);
			Assert.Equal("Local", text.Provenance);

			// The caption's declaration is three attributes, and exactly those three come back. The
			// UIElement composition properties -- CenterPoint, Rotation, Scale and the rest -- read as
			// BaseValueSourceLocal the moment the framework touches one, so they were reported as six
			// local sets that the markup does not make, crowding out the properties that matter.
			var composition = new[] { "CenterPoint", "Rotation", "RotationAxis", "Scale", "TransformMatrix", "Translation" };
			Assert.DoesNotContain(captionProperties.Properties, property => composition.Contains(property.Name));

			// Still available to anyone who asks for everything on the element; just not offered as
			// evidence of what the XAML sets.
			var captionDefaults = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: true, cancellationToken);
			Assert.Contains(captionDefaults.Properties, property => property.Name == "Scale");

			// No property claims a location it cannot support. XAML diagnostics reports source info per
			// source object, not per property, so for a value set on the element the only location
			// available is the element's own tag -- which the element carries, and which was being
			// copied onto every property. That made a genuine attribution byte-identical to a
			// fabricated one, so it is emitted only when the source is something other than the element.
			Assert.All(
				captionProperties.Properties.Where(property => property.Provenance == "Local"),
				property => Assert.Null(property.SourceFile));

			// The element's own declaration is real, and is where it has always belonged.
			Assert.Equal("ms-appx:///MainPage.xaml", captionProperties.SourceFile);
			Assert.Equal(17, captionProperties.SourceLine);
		}
	}

	/// <summary>
	/// #21: a CornerRadius came back as an empty string while the Thickness beside it, set by the same
	/// markup, came back as "24,24,24,24". Not our formatting -- the framework populates that BSTR
	/// itself and populated it with nothing -- so it is read off the element instead.
	/// <para>
	/// A sweep of every property of every element in this app settled how far to go: 3,485 rows, 32 of
	/// them empty while not null, and of those the only struct type was CornerRadius. So one per-type
	/// special case rather than twenty, and a flag for whatever the next one turns out to be --
	/// <c>CornerRadiusProtected</c> is already it, being protected and absent from the projection.
	/// </para>
	/// <para>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(2)]
	public async Task Reads_a_corner_radius_the_framework_renders_as_nothing()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C: builds what it reads in a slot of its own. The values are the ones the shared Pane
		// carries, but owning the element means this cannot be disturbed by a test editing that one,
		// and reading it cannot change what such a test sees.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup,
				turn.MarkupHolding("<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"8\" />"),
				filePath: null,
				cancellationToken);
			Assert.True(built.Detail is null, $"expected the slot to be filled, got detail: {built.Detail}");

			var subtree = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var pane = subtree.Nodes.FirstOrDefault(node => node.Address == turn.Address("Border[0]"));
			Assert.NotNull(pane);

			var properties = await session.ReadXamlPropertiesAsync(pane!.Handle, includeDefaults: false, cancellationToken);
			Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");

			// The markup says CornerRadius="8", and it reads back in the same four-number form the
			// Thickness beside it uses, so a caller that parses one parses the other.
			var radius = properties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");
			Assert.NotNull(radius);
			Assert.Equal("8,8,8,8", radius!.Value);
			Assert.False(radius.ValueUnavailable, "a corner radius of zero is a value rather than an absence");

			var padding = properties.Properties.FirstOrDefault(property => property.Name == "Padding");
			Assert.NotNull(padding);
			Assert.Equal("24,24,24,24", padding!.Value);

			// The other half: something the framework will not render, and cannot be read a second way
			// because it is protected and not in the projection, says so rather than looking unset.
			// An empty value that means two different things is how the CornerRadius case hid.
			// Named rather than taken as the first row: under a shared app the tree's order is not this
			// test's to rely on, and "whatever came back first" is the sort of assumption that fails
			// later for a reason nobody connects to this line.
			var whole = await session.ReadXamlTreeAsync(cancellationToken);
			var root = whole.Nodes.Single(node => node.Name == "RootGrid");
			var all = await session.ReadXamlPropertiesAsync(root.Handle, includeDefaults: true, cancellationToken);
			var unavailable = all.Properties.Where(property => property.ValueUnavailable).ToArray();

			Assert.All(unavailable, property => Assert.Equal(string.Empty, property.Value));

			// And a string that is genuinely empty is not flagged, or the flag fires on the majority of
			// empty values and stops meaning anything.
			Assert.DoesNotContain(
				all.Properties.Where(property => property.ValueType == "Windows.Foundation.String"),
				property => property.ValueUnavailable);
		}
	}

	/// <summary>
	/// #22: a UWP session names the install location it actually got.
	/// <para>
	/// Two layouts can sit under one identity and version -- a stale <c>Release\AppX</c> registered
	/// while a fresh <c>Debug\AppX</c> is on disk -- because <c>Add-AppxPackage -Register</c> silently
	/// does nothing when a package of the same identity is already registered. Everything downstream
	/// then describes the build nobody meant to run, and describes it accurately. The install location
	/// is the one field that makes that visible, so it is asserted to be there and to be the layout
	/// this test staged rather than merely non-null.
	/// </para>
	/// <para>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </para>
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Names_the_install_location_a_uwp_session_activated()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp install-location probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);

			// On the session, which is what a caller reading one result sees.
			var summary = session.Describe();
			Assert.NotNull(summary.InstallLocation);
			Assert.Equal(
				Path.GetFullPath(probe.LayoutDirectory!).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(summary.InstallLocation!).TrimEnd(Path.DirectorySeparatorChar),
				ignoreCase: true);

			// And in the event stream, where somebody reading what happened sees it at the moment it
			// mattered rather than having to go and ask.
			var events = await session.ReadEventsAsync(0, null, 500, cancellationToken);
			Assert.Contains(
				events.Events,
				entry => entry.Kind == LiveDebugEventKind.SessionNotice
					&& (entry.Message?.Contains("is registered from", StringComparison.Ordinal) ?? false));

			// And on the tree, because that is the tool that answers plausibly rather than failing:
			// its nodes carry source files, and a stale registration makes those the wrong files.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
			Assert.Equal(summary.InstallLocation, tree.InstallLocation);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// XAML live editing (#12): diff two versions of the probe's XAML and apply the changes to the live
	/// tree, no relaunch. Two edits, deliberately, because they fail in different ways:
	/// <list type="bullet">
	/// <item>
	/// The caption's font size is a Double, the straightforward case, and it is confirmed by reading
	/// the live value back.
	/// </item>
	/// <item>
	/// The pane's corner radius is a struct whose value is a single number, which the diff engine's
	/// name-and-shape inference called a Double until it was told otherwise -- and a value built as
	/// the wrong type is created quite happily and only fails at SetProperty, with an E_FAIL that
	/// names nothing. This is the end-to-end guard for that, and for the provider's fallback to the
	/// property's own declared type. It is asserted by reading the value back as well as through
	/// its status: reading it back was impossible until #21, because the framework hands us an
	/// empty string for a CornerRadius value where Thickness stringifies.
	/// </item>
	/// </list>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </summary>
	[Test]
	[ClassicSlot(3)]
	public async Task Live_edits_a_property_on_the_uwp_probe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C: both edits land on elements this test built in its own slot, so it neither disturbs
		// the app's furniture nor depends on that furniture still carrying the values the markup gave
		// it. The two kinds of value are the point -- a Double and a struct -- not which elements
		// happen to carry them.
		const string Before =
			"<TextBlock Text=\"Rose UWP Probe\" FontSize=\"24\" />"
				+ "<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"8\" />";
		const string After =
			"<TextBlock Text=\"Rose UWP Probe\" FontSize=\"40\" />"
				+ "<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"0\" />";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Before), filePath: null, cancellationToken);
			Assert.True(built.Detail is null, $"expected the slot to be filled, got detail: {built.Detail}");

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Before), turn.MarkupHolding(After), filePath: null, cancellationToken);
			Assert.True(applied.Detail is null, $"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("TextBlock[0]") && result.Property == "FontSize");
			Assert.NotNull(edit);
			Assert.Equal("applied", edit!.Status);

			// The struct-valued edit, which is the one that used to come back "SetProperty failed
			// 0x80004005" because it had been built as a Double.
			var radius = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("Border[0]") && result.Property == "CornerRadius");
			Assert.NotNull(radius);
			Assert.Equal("applied", radius!.Status);

			Assert.Equal(2, applied.Applied);

			// The live element actually changed: reading its font size back gives the new value.
			var tree = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var caption = tree.Nodes.Single(node => node.Address == turn.Address("TextBlock[0]"));
			var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
			var fontSize = properties.Properties.FirstOrDefault(property => property.Name == "FontSize");
			Assert.NotNull(fontSize);
			Assert.Equal("40", fontSize!.Value);

			// And the struct-valued edit is now read back too, rather than trusted from its status.
			// It used to be asserted only through "applied", because a CornerRadius came back as an
			// empty string -- which is #21, and is fixed, so the weaker assertion has no reason left.
			var pane = tree.Nodes.Single(node => node.Address == turn.Address("Border[0]"));
			var paneProperties = await session.ReadXamlPropertiesAsync(pane.Handle, includeDefaults: false, cancellationToken);
			var cornerRadius = paneProperties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");
			Assert.NotNull(cornerRadius);
			Assert.Equal("0,0,0,0", cornerRadius!.Value);
		}
	}

	/// <summary>
	/// Addressing an element the markup never named (#11). Every element in the probe app used to carry
	/// an <c>x:Name</c>, so nothing here could reach the case that matters most: a click inside a
	/// control template lands on an element with no name, and until now there was no way to say which
	/// element was meant -- the apply refused it, and a caller only found that out after composing a
	/// whole before-and-after XAML pair. <c>#Pair/Border[1]</c> is the way to say it.
	/// <para>
	/// The negative assertion is the one that earns the test. Resolving <em>an</em> element proves
	/// nothing, because the failure this replaces put the change on a plausible neighbour and reported
	/// success either way -- so the first Border is read back as well, and has to be untouched.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(4)]
	public async Task Live_edits_an_unnamed_element_by_its_address()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// The second of two unnamed siblings, which is the case this exists for: an element the markup
		// never named, told from its twin by position under a named anchor. The anchor is this test's
		// own slot rather than the app's Pair, so the two Borders it counts are the only two there.
		const string FirstBackground = "#FF3A2A2A";
		const string SecondBackground = "#FF2A3A2A";
		const string ChangedBackground = "#FF00FF00";
		const string Pair =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />"
				+ "<Border Background=\"" + SecondBackground + "\" Padding=\"6\" CornerRadius=\"3\" />";
		const string Changed =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />"
				+ "<Border Background=\"" + ChangedBackground + "\" Padding=\"6\" CornerRadius=\"3\" />";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Pair), filePath: null, cancellationToken);
			Assert.True(built.Detail is null, $"expected the slot to be filled, got detail: {built.Detail}");

			// The provider derives an address from the live tree, so this half stands on its own: two
			// unnamed siblings of one type are told apart by their position under the named anchor.
			var before = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var addresses = before.Nodes.Select(node => node.Address).ToList();
			Assert.Contains(turn.Address("Border[0]"), addresses);
			Assert.Contains(turn.Address("Border[1]"), addresses);

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Pair), turn.MarkupHolding(Changed), filePath: null, cancellationToken);
			Assert.True(applied.Detail is null, $"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("Border[1]") && result.Property == "Background");
			Assert.NotNull(edit);
			Assert.Equal("applied", edit!.Status);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Equal(ChangedBackground, await BackgroundAtAsync(session, tree, turn.Address("Border[1]"), cancellationToken));
			Assert.Equal(FirstBackground, await BackgroundAtAsync(session, tree, turn.Address("Border[0]"), cancellationToken));
		}
	}

	/// <summary>
	/// Removing an element live (#11). <c>IVisualTreeService::RemoveChild</c> takes a parent and a
	/// <em>position</em>, while a diff knows only that a child present in the old markup is absent from
	/// the new one -- so the provider resolves the child's address and reads the parent and the index off
	/// the live tree, which is the only place they can be trusted.
	/// <para>
	/// Which Border survives is the assertion that earns this. The two are identical but for their
	/// colour, so an index off by one removes the wrong one and everything else still reads as success:
	/// one Border left under the anchor, the edit reported <c>applied</c>, and the wrong element gone.
	/// The survivor's Background is what tells them apart.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(5)]
	public async Task Removes_an_element_from_the_live_tree()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Two unnamed siblings in this test's own slot, and the second one goes. Built here rather than
		// cut out of the app's markup: a removal is the edit that most needs to be nobody else's, since
		// the index it resolves against is a position among siblings.
		//
		// It used to take the app exclusively as well, because it passed alone and failed in company
		// reporting the removal applied while both Borders were still there. That was never a
		// concurrency problem and exclusivity never fixed it, only hid it: the fixture's own slot
		// cleanup emitted its two removals in document order, the first renumbered the second, and the
		// slot was handed on still holding an element this test then counted as one of its own. The
		// ordering is fixed in XamlDiff and the cleanup checks itself now (D36), so a slot is enough.
		const string FirstBackground = "#FF3A2A2A";
		const string Pair =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />"
				+ "<Border Background=\"#FF2A3A2A\" Padding=\"6\" CornerRadius=\"3\" />";
		const string Survivor =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Pair), filePath: null, cancellationToken);
			Assert.True(built.Detail is null, $"expected the slot to be filled, got detail: {built.Detail}");

			var before = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			Assert.Equal(2, before.Nodes.Count(node => node.Address == turn.Address("Border[0]") || node.Address == turn.Address("Border[1]")));

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Pair), turn.MarkupHolding(Survivor), filePath: null, cancellationToken);
			Assert.True(applied.Detail is null, $"expected an apply, got detail: {applied.Detail}");

			var removal = applied.Results.FirstOrDefault(result => result.Kind == "RemoveChild");
			Assert.NotNull(removal);
			Assert.Equal(turn.Address("Border[1]"), removal!.Target);
			Assert.Equal("applied", removal.Status);

			// Read back off a fresh enumeration, so this checks the app rather than the provider's own
			// bookkeeping: every injection builds a new tap and walks the tree again.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var remaining = tree.Nodes
				.Where(node => node.Address == turn.Address("Border[0]") || node.Address == turn.Address("Border[1]"))
				.ToList();
			var survivor = Assert.Single(remaining);
			Assert.Equal(turn.Address("Border[0]"), survivor.Address);

			// And it is the one that was meant to stay.
			Assert.Equal(FirstBackground, await BackgroundAtAsync(session, tree, turn.Address("Border[0]"), cancellationToken));

		}
	}

	/// <summary>
	/// The card's acceptance criterion for #11, whole: a diff that adds a child, removes a child and
	/// changes a non-brush property, applied live in one call.
	/// <para>
	/// Adding is the piece that cannot be one command. There is no way to apply markup -- CreateInstance
	/// builds one object from a type name -- so the subtree is decomposed into build steps and the
	/// element is assembled off the tree before anything attaches it. What this checks is that the
	/// assembled element arrives complete: it is not enough for it to exist, so its own property is read
	/// back off the running app, and so is the nested child it was given.
	/// </para>
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Adds_removes_and_retypes_in_one_apply()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = lease.Aumid;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		// The added element is deliberately a different type from the removed one. A diff is minimal, so
		// swapping a Border for a Border is a property change and no structural edit happens at all --
		// which is right, and not what this test is for.
		//
		// The two types are chosen to exercise both halves of resolving a name for CreateInstance. The app
		// already has a Grid, so that one is answered from the types the live tree reports; it has no
		// Rectangle anywhere, and Shapes is not the namespace Controls live in, so that one can only come
		// from the framework namespaces tried afterwards.
		const string SecondBorder = "<Border Background=\"#FF2A3A2A\"";
		const string Added =
			"<Grid Background=\"#FF00FFFF\"><Rectangle Fill=\"#FFFF00FF\" Width=\"10\" Height=\"10\" /></Grid>";

		var start = oldXaml.IndexOf(SecondBorder, StringComparison.Ordinal);
		Assert.True(start >= 0, "the probe markup no longer holds the second unnamed Border");
		var close = oldXaml.IndexOf("</Border>", start, StringComparison.Ordinal) + "</Border>".Length;

		var newXaml = oldXaml
			.Remove(start, close - start)
			.Insert(start, Added)
			.Replace("Text=\"ticks: 0\"", "Text=\"ticks: 0\" Opacity=\"0.5\"");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp add probe",
				},
				cancellationToken);

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
			Assert.True(applied.Detail is null, $"expected an apply, got detail: {applied.Detail}");

			var add = applied.Results.FirstOrDefault(result => result.Kind == "AddChild");
			Assert.NotNull(add);
			Assert.Equal("applied", add!.Status);

			var removal = applied.Results.FirstOrDefault(result => result.Kind == "RemoveChild");
			Assert.NotNull(removal);
			Assert.Equal("applied", removal!.Status);

			// The non-brush property, on a named element, so all three kinds are in the one apply.
			var opacity = applied.Results.FirstOrDefault(result => result.Property == "Opacity");
			Assert.NotNull(opacity);
			Assert.Equal("applied", opacity!.Status);

			// The added element is in the app, and it arrived built rather than merely present: its own
			// property is set, and the child it was given is underneath it. Existing is not complete.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Equal("#FF00FFFF", await BackgroundAtAsync(session, tree, "#Pair/Grid[0]", cancellationToken));

			var nested = tree.Nodes.SingleOrDefault(node => node.Address == "#Pair/Grid[0]/Rectangle[0]");
			Assert.NotNull(nested);
			Assert.EndsWith("Rectangle", nested!.TypeName, StringComparison.Ordinal);

			// And the removal happened: one Border left under the anchor, not two.
			Assert.Single(tree.Nodes, node => node.Address is "#Pair/Border[0]" or "#Pair/Border[1]");

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// Attached properties on the apply side (#11). The diff has always kept <c>Grid.Row</c> by its
	/// dotted name, which is a different question from whether the provider can find one: an attached
	/// property is not declared on the element it is set on, so whether it turns up in that element's
	/// property chain under the same spelling the markup used is a fact about the framework and had
	/// never been asked.
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Sets_an_attached_property_on_the_live_tree()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = lease.Aumid;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		Assert.Contains("Grid.Row=\"0\"", oldXaml);
		var newXaml = oldXaml.Replace("Grid.Row=\"0\"", "Grid.Row=\"1\"");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp attached probe",
				},
				cancellationToken);

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
			Assert.True(applied.Detail is null, $"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(result => result.Property == "Grid.Row");
			Assert.NotNull(edit);
			Assert.Equal("#Attached", edit!.Target);
			Assert.Equal("applied", edit.Status);

			// Read back off the app, because "applied" only says SetProperty returned S_OK.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var element = tree.Nodes.Single(node => node.Name == "Attached");
			var properties = await session.ReadXamlPropertiesAsync(element.Handle, includeDefaults: false, cancellationToken);
			var row = properties.Properties.FirstOrDefault(property => property.Name == "Grid.Row");
			Assert.NotNull(row);
			Assert.Equal("1", row!.Value);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// Resource-dictionary edits (#11), the last thing on the card. A resource is keyed rather than
	/// positional, and it is not an element in the visual tree at all -- a <c>*.Resources</c> block is a
	/// property written in element form -- so before this the diff addressed one as
	/// <c>Grid[0]/Grid.Resources[0]/SolidColorBrush[0]</c> and the apply failed complaining about a
	/// missing element, which is the wrong problem stated confidently.
	/// <para>
	/// The assertion that matters is the second one: the element already using the key. Replacing what a
	/// key resolves to is only worth anything if what was drawn from it follows, and that is a fact
	/// about the framework rather than about this code -- which is why the probe references the brush
	/// with <c>ThemeResource</c>, the form that re-evaluates.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Replaces_a_keyed_resource_on_the_live_tree()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		const string Was = "#FF445566";
		const string Now = "#FFAA3300";
		Assert.Contains($"x:Key=\"ProbeAccent\" Color=\"{Was}\"", oldXaml);
		var newXaml = oldXaml.Replace($"x:Key=\"ProbeAccent\" Color=\"{Was}\"", $"x:Key=\"ProbeAccent\" Color=\"{Now}\"");

		{

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			// The element is drawing the old colour through the key before anything is changed.
			var before = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Equal(Was, await BackgroundAtAsync(session, before, "#Themed", cancellationToken));

			var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
			Assert.True(applied.Detail is null, $"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(result => result.Kind == "SetResource");
			Assert.NotNull(edit);
			Assert.Equal("#RootGrid", edit!.Target);
			Assert.Equal("ProbeAccent", edit.Property);
			Assert.Equal("applied", edit.Status);

			// And the element that resolves the key follows it.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Equal(Now, await BackgroundAtAsync(session, tree, "#Themed", cancellationToken));

		}
	}

	/// <summary>The Background of whichever live element carries an address, read off the tree.</summary>
	private static async Task<string?> BackgroundAtAsync(
		LiveAppSession session,
		LiveXamlTree tree,
		string address,
		CancellationToken cancellationToken)
	{
		var node = tree.Nodes.Single(candidate => candidate.Address == address);
		var properties = await session.ReadXamlPropertiesAsync(node.Handle, includeDefaults: false, cancellationToken);
		return properties.Properties.FirstOrDefault(property => property.Name == "Background")?.Value;
	}

	/// <summary>
	/// The edit-to-live loop (#12): edit a XAML file, apply, edit it again, apply again, and the running
	/// app follows -- with nothing carried between the calls but the path of the file.
	/// <para>
	/// Every apply test before this one passed both versions of the markup, which is the shape the tool
	/// started with and close to unusable in the loop it exists for, since a caller that has just
	/// written a file no longer holds what was in it. So the session remembers what it last sent, and
	/// the assertion that earns this test is the <em>count</em> on the second edit: it changes a
	/// different property from the first, so a baseline still sitting on the original would come back
	/// with two edits rather than one. Re-sending an edit is harmless for a font size and, for an added
	/// element, a second copy of it.
	/// </para>
	/// <para>
	/// It edits a copy of the probe's markup rather than the file itself. What is under test is the
	/// file-to-diff-to-apply-to-live path, which a copy exercises identically; editing the fixture in
	/// place would leave a tracked file modified if this failed part way through, and the sibling tests
	/// read that file expecting what is checked in.
	/// </para>
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Applies_successive_file_edits_to_the_running_app()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = lease.Aumid;

		var sourcePath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var original = File.ReadAllText(sourcePath);
		var editable = Path.Combine(Path.GetTempPath(), $"rose-reload-{Guid.NewGuid():N}.xaml");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp continuous live-edit probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			// The app's own markup, untouched since it was launched. There is nothing to apply, and
			// this side says so with evidence rather than by diffing the file against itself -- which
			// would report nothing either, and would mean something else entirely.
			var first = await session.ApplyXamlAsync(null, null, sourcePath, cancellationToken);
			Assert.True(first.Detail is null, $"expected a baseline, got detail: {first.Detail}");
			Assert.Empty(first.Results);
			Assert.Contains(first.Notes, note => note.Contains("Nothing has edited") && note.Contains("MainPage.xaml"));

			// A file that has changed since the app started is the other first-apply case: what the
			// app was built from is gone, so it records the file and says so rather than guessing.
			File.WriteAllText(editable, original);
			var registered = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			Assert.True(registered.Detail is null, $"expected a baseline, got detail: {registered.Detail}");
			Assert.Empty(registered.Results);
			Assert.Contains(registered.Notes, note => note.Contains("no longer on disk"));

			// One edit, applied with nothing passed but the path.
			File.WriteAllText(editable, original.Replace("FontSize=\"24\"", "FontSize=\"40\""));
			var fontSize = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			Assert.True(fontSize.Detail is null, $"expected an apply, got detail: {fontSize.Detail}");
			var sizeEdit = Assert.Single(fontSize.Results);
			Assert.Equal("#Caption", sizeEdit.Target);
			Assert.Equal("FontSize", sizeEdit.Property);
			Assert.Equal("applied", sizeEdit.Status);
			Assert.Equal("40", await CaptionValueAsync(session, "FontSize", cancellationToken));

			// A second edit, same session, no relaunch -- and a different property, which is what makes
			// the single result below mean the baseline moved with the first apply.
			File.WriteAllText(
				editable,
				original
					.Replace("FontSize=\"24\"", "FontSize=\"40\"")
					.Replace("Text=\"Rose UWP Probe\"", "Text=\"Edited twice\""));

			var caption = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			Assert.True(caption.Detail is null, $"expected an apply, got detail: {caption.Detail}");
			var textEdit = Assert.Single(caption.Results);
			Assert.Equal("#Caption", textEdit.Target);
			Assert.Equal("Text", textEdit.Property);
			Assert.Equal("applied", textEdit.Status);

			// Both edits are on the running app: the second landed, and the first is still there rather
			// than having been undone by a diff that started over from the original.
			Assert.Equal("Edited twice", await CaptionValueAsync(session, "Text", cancellationToken));
			Assert.Equal("40", await CaptionValueAsync(session, "FontSize", cancellationToken));

			// And an apply with nothing to apply says which of the two nothings it was.
			var unchanged = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			Assert.True(unchanged.Detail is null, $"expected an apply, got detail: {unchanged.Detail}");
			Assert.Empty(unchanged.Results);
			Assert.Contains(unchanged.Notes, note => note.Contains("unchanged since the last apply"));

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
			if (File.Exists(editable)) File.Delete(editable);
		}
	}

	/// <summary>
	/// Two XAML calls in flight together (#93). The host serves MCP calls concurrently -- measured
	/// rather than assumed: two tree reads issued together finished in the time of one, where
	/// serialised they take twice as long -- and everything behind them shares one work folder, one
	/// request.txt and one generation counter.
	/// <para>
	/// What that produced, over ten concurrent pairs against this probe: a request.txt that could not
	/// be written because the other call held it, several fifteen-second waits for a snapshot the
	/// other call's injection had already consumed, and once <em>a tree of 22 elements where the app
	/// has 24, returned with no detail set</em>. The last one is why this is a test and not a note in
	/// the docs. A truncated tree reported as success hands out handles for a tree that is not there,
	/// and nothing downstream can tell.
	/// </para>
	/// <para>
	/// Repeated, because one pair landing well says nothing -- the silent failure appeared once in
	/// ten. Asserted on the answers and not on timing: the fix makes these calls queue, so timing is
	/// what changed, but a duration assertion would be flaky and slower is not the property worth
	/// protecting. A tree read paired with a property read is the pair most likely to expose it, since
	/// the two want different content in the one request file.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Serves_two_xaml_calls_in_flight_together()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// Counted without the probe's Transient pair, which is the one thing in this app that
			// changes on its own: it leaves the visual tree and comes back on a five-second cycle, by
			// design, so that #51 has a removal to watch. Two elements go with it, and this test reads
			// the tree ten times over several seconds -- so a fixed whole-tree count was a coin flip
			// against a one-second-in-five window, and it came up 43 and then 41. What this protects is
			// that a concurrent read is not silently *truncated* (22 elements where the app has 24),
			// and every stable element being present says that just as well.
			static int Stable(LiveXamlTree tree) =>
				tree.Nodes.Count(node => node.Name is not ("Transient" or "TransientCaption"));

			// The answer every concurrent read below has to match. Read on its own, so it is the
			// uncontended truth about the app rather than one of the results under test.
			var alone = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(alone.Detail is null, $"expected a tree, got detail: {alone.Detail}");
			var expected = Stable(alone);
			Assert.True(expected > 1, $"the probe should have more than one element, got {expected}");
			var caption = alone.Nodes.First(node => node.Name == "Caption");

			for (var attempt = 0; attempt < 5; attempt++)
			{
				var first = session.ReadXamlTreeAsync(cancellationToken);
				var second = session.ReadXamlTreeAsync(cancellationToken);
				var trees = await Task.WhenAll(first, second);

				foreach (var tree in trees)
				{
					Assert.True(tree.Detail is null, $"attempt {attempt}: expected a tree, got detail: {tree.Detail}");
					Assert.Equal(expected, Stable(tree));
				}
			}

			for (var attempt = 0; attempt < 5; attempt++)
			{
				var treeTask = session.ReadXamlTreeAsync(cancellationToken);
				var propertiesTask = session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
				var tree = await treeTask;
				var properties = await propertiesTask;

				Assert.True(tree.Detail is null, $"attempt {attempt}: expected a tree, got detail: {tree.Detail}");
				Assert.Equal(expected, Stable(tree));

				Assert.True(properties.Detail is null, $"attempt {attempt}: expected properties, got detail: {properties.Detail}");
				Assert.Equal(caption.Handle, properties.Handle);
				Assert.NotEmpty(properties.Properties);

				// The element it answered about, rather than only the handle it echoed. How many
				// properties come back is deliberately not asserted: that count is not stable across
				// repeat reads even without concurrency, which is its own defect and not this one.
				Assert.Contains(properties.Properties, property => property.Name == "Text");
			}

		}
	}

	/// <summary>
	/// What a second read of an element's properties reports (#97). Not the behaviour anyone would
	/// choose -- it is the behaviour there is, pinned so it stops being a surprise.
	/// <para>
	/// Reading an element's property chain brings its untouched collection properties into existence,
	/// and a property that exists is no longer the framework's default. So the second read of a
	/// <c>TextBlock</c> reports <c>Inlines</c>, <c>TextHighlighters</c> and
	/// <c>SelectionHighlightColor</c> as <c>Local</c>, with provenance and values as plausible as the
	/// ones the markup really set. The first read is the accurate one, and it is our own read that
	/// spoils it -- which also means <c>rose_xaml_properties</c> is declared read-only and is not
	/// quite, though nothing the app draws changes.
	/// </para>
	/// <para>
	/// Measured before it was documented: the three extras arrive with <c>provenance=Local</c>, so
	/// there is no source to filter on and the one-line fix does not exist. A <c>Border</c> is stable
	/// across reads, so this is TextBlock's text properties rather than something general -- which is
	/// why the assertions below are about shape and not about a fixed list of names.
	/// </para>
	/// <para>
	/// One fix must not be attempted, and it is the tempting one: caching the first read's names and
	/// filtering later reads to them would hide exactly what the apply-then-read-back loop of #12
	/// exists to verify, since an applied property need not have appeared in the first read.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(6)]
	public async Task A_second_properties_read_reports_what_reading_the_first_created()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C, and the awkward one. The whole assertion is about what the *first* properties read
		// of an element returns, so it needs an element nothing has read -- and there are two ways to
		// fail that, not one. Sharing the app's Caption would make it depend on running before every
		// other test that reads a TextBlock. Building its own in a slot does not work either: an
		// element created through CreateInstance and AddChild arrives with Inlines already
		// materialised, so it was never pristine to begin with. Only markup declares an element
		// nothing has touched, so it reads a declared one that belongs to it alone. The Border it
		// compares against can be built, because a Border has no collection property to materialise
		// -- which is the point that test is making.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup,
				turn.MarkupHolding("<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"8\" />"),
				filePath: null,
				cancellationToken);
			Assert.True(built.Detail is null, $"expected the slot to be filled, got detail: {built.Detail}");

			var whole = await session.ReadXamlTreeAsync(cancellationToken);
			var caption = whole.Nodes.Single(node => node.Name == "PristineText");
			var pane = whole.Nodes.Single(node => node.Address == turn.Address("Border[0]"));

			// A tree read does not do it -- only a properties read of that element does -- so this
			// first read of the caption is still the markup's own answer.
			var first = await NamesOfAsync(session, caption.Handle, cancellationToken);
			Assert.Equal(["FontSize", "Foreground", "Text"], first);

			var second = await NamesOfAsync(session, caption.Handle, cancellationToken);

			// A superset, never a different set: nothing the markup set may disappear.
			Assert.All(first, name => Assert.Contains(name, second));
			Assert.True(second.Count > first.Count, $"expected the second read to grow, got {second.Count}");

			// And the additions are indistinguishable by provenance, which is the finding that
			// decided against filtering. If this ever fails because an addition arrives as something
			// other than Local, there is a filter to write and #97 can be fixed properly.
			var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
			foreach (var added in second.Except(first))
			{
				var property = properties.Properties.Single(candidate => candidate.Name == added);
				Assert.Equal("Local", property.Provenance);
			}

			// A Border has no collection property to materialise, so it does not move. This is what
			// makes the behaviour a property of the element's type rather than of reading as such.
			var paneFirst = await NamesOfAsync(session, pane.Handle, cancellationToken);
			var paneSecond = await NamesOfAsync(session, pane.Handle, cancellationToken);
			Assert.Equal(paneFirst, paneSecond);
		}
	}

	/// <summary>The names of one element's set properties, ordered so two reads can be compared.</summary>
	private static async Task<List<string>> NamesOfAsync(
		LiveAppSession session,
		ulong handle,
		CancellationToken cancellationToken)
	{
		var properties = await session.ReadXamlPropertiesAsync(handle, includeDefaults: false, cancellationToken);
		Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");

		return [.. properties.Properties.Select(property => property.Name).Order(StringComparer.Ordinal)];
	}

	/// <summary>One of the probe caption's live property values, read off the running app.</summary>
	private static async Task<string?> CaptionValueAsync(
		LiveAppSession session,
		string property,
		CancellationToken cancellationToken)
	{
		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		var caption = tree.Nodes.First(node => node.Name == "Caption");
		var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);

		return properties.Properties.FirstOrDefault(candidate => candidate.Name == property)?.Value;
	}

	/// <summary>
	/// Interactive selection (#18): every XAML tool leaves RoseMCP's toolbar resident on the app's
	/// diagnostics UI layer, the tree snapshot keeps that toolbar out of its answer, and arming select
	/// mode is reported by the provider rather than assumed here. Until someone clicks, the selection is
	/// empty rather than stale or invented -- the click is a human action, so this stops at the armed
	/// state rather than driving the mouse on a live desktop.
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Arms_interactive_select_mode_on_the_classic_uwp_probe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// Any XAML tool installs the toolbar, and the tree must not report it: it is RoseMCP's UI,
			// not the app's. Read the tree first so the toolbar is up, then read it again and check.
			var beforeToolbar = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(beforeToolbar.Detail is null, $"expected a tree, got detail: {beforeToolbar.Detail}");

			var withToolbar = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.DoesNotContain(withToolbar.Nodes, node => node.Name == "__RoseMcpOverlay");
			Assert.Contains(withToolbar.Nodes, node => node.Name == "Caption");

			// The provider confirms select mode armed, rather than the host assuming it.
			// The framework's own hit test, which is the default and the only sane one: with
			// includeAllElements a background-less Grid stretched over the window shadows every
			// element the user can actually click.
			var selectMode = await session.EnterXamlSelectModeAsync(includeAllElements: false, justMyXaml: true, cancellationToken);
			Assert.True(
				selectMode.Armed,
				$"expected select mode to arm; got: {selectMode.Detail}");
			Assert.True(selectMode.JustMyXaml);

			// Arming reports the preference it was actually given. It used to leave the field to the
			// record's default of true, so arming with false answered true, and a caller comparing the
			// arming response against a later selection saw a contradiction with no explanation. A
			// field session hit exactly that and talked itself out of it with a plausible theory about
			// arm-time preference versus what decided the pick -- which was not what the code did.
			var withoutFilter = await session.EnterXamlSelectModeAsync(includeAllElements: false, justMyXaml: false, cancellationToken);
			Assert.True(withoutFilter.Armed, $"expected select mode to arm; got: {withoutFilter.Detail}");
			Assert.False(withoutFilter.JustMyXaml, "an unfiltered read reports no just-my-xaml filter");

			// And the toolbar agrees, because it is one switch rather than two pieces of state.
			var afterDisabling = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(afterDisabling.JustMyXaml, "disabling the filter turns it off");

			// Nothing picked yet: an empty selection that says so, safe to poll. Armed comes back from
			// the toolbar's own state file, so this is the provider reporting, not the host remembering.
			var selection = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(selection.Selected, "select mode arms with nothing selected");
			Assert.True(selection.Armed, $"expected the toolbar to report select mode armed; got: {selection.Detail}");
			Assert.NotNull(selection.Detail);

			// #45: the pick can be cleared, and clearing says which of "cleared" and "there was nothing
			// selected" happened rather than treating both as success. Nothing has been picked here --
			// a click is a human action and this suite does not drive the mouse on a live desktop -- so
			// the second is the honest answer, and it is the one that used to be unreachable at all.
			var cleared = await session.ClearXamlSelectionAsync(cancellationToken);
			Assert.False(cleared.Selected, "deselecting clears the selection");
			Assert.Contains("nothing", cleared.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

			// And the toolbar is still there afterwards, because deselecting is not leaving.
			var afterClearing = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(afterClearing.Selected, "clearing again leaves nothing selected");

			// Armed is app-wide state this test turned on, so this test turns it off. Clearing the
			// pick does not disarm, because they are two pieces of state -- and until the shared app
			// made it matter, nothing here could disarm at all: there was no verb for it, so an agent
			// that armed select mode left a pointer-capturing overlay on the app that only a person
			// clicking Idle could lift.
			var idle = await session.EnterXamlSelectModeAsync(
				includeAllElements: false, justMyXaml: true, arm: false, cancellationToken);
			Assert.False(idle.Armed, $"expected select mode to disarm; got: {idle.Detail}");

		}
	}

	/// <summary>
	/// #46: selecting by handle, with no hit test in the path. That is what reaches a control a click
	/// cannot -- a slider is the reported case, and what a click resolves to is the framework's answer
	/// rather than ours -- and it is the only way this suite can make a selection at all, since a
	/// click is a human action and nothing here drives the mouse on a live desktop.
	/// <para>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Selects_a_xaml_element_by_handle_without_a_click()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// The handle comes from the tree, which is the whole route this exists to open.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var pane = tree.Nodes.FirstOrDefault(node => node.Name == "Pane");
			Assert.NotNull(pane);

			var selected = await session.SelectXamlElementAsync(pane!.Handle, cancellationToken);

			Assert.True(selected.Selected, $"expected a selection; got: {selected.Detail}");
			Assert.Equal(pane.Handle, selected.Handle);
			Assert.Equal("Pane", selected.Name);

			// The stack is the element then its ancestors outwards, so a caller who took the handle the
			// tree gave them can still reach the container they actually meant.
			Assert.Contains(selected.Candidates, candidate => candidate.Name == "Panel");
			Assert.Contains(selected.Candidates, candidate => candidate.Name == "RootGrid");

			// It reads back through the same path a click produces, which is the point of writing the
			// same files: one read path, whichever route made the selection.
			var reread = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.True(reread.Selected);
			Assert.Equal(pane.Handle, reread.Handle);

			// And the handle it hands back drives the rest of the surface without another round trip.
			var properties = await session.ReadXamlPropertiesAsync(selected.Handle, includeDefaults: false, cancellationToken);
			Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");
			Assert.Contains(properties.Properties, property => property.Name == "CornerRadius");

			// Clearing it works the same as for a click, because it is the same selection (#45).
			var cleared = await session.ClearXamlSelectionAsync(cancellationToken);
			Assert.False(cleared.Selected, "deselecting clears a selection made by handle");
			Assert.Contains("cleared", cleared.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

		}
	}

	/// <summary>
	/// A handle that names something real but not an element -- a Brush has one too -- is refused with
	/// a sentence rather than drawing an outline round nothing.
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Refuses_to_select_a_handle_that_is_not_an_element()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			await session.ReadXamlTreeAsync(cancellationToken);

			// A handle nothing owns. The provider resolves it, finds nothing, and declines.
			var selected = await session.SelectXamlElementAsync(1, cancellationToken);

			Assert.False(selected.Selected, "a refused selection selects nothing");
			Assert.NotNull(selected.Detail);

		}
	}

	/// <summary>
	/// #51: a selection whose element leaves the visual tree is cleared, and says why.
	/// <para>
	/// The selection is the one mark that outlives the interaction which drew it, so it was the one
	/// mark with nothing watching it -- the outline stayed where it was while pointing at nothing, and
	/// the recorded handle stayed too, so the next properties call failed with a diagnostics HRESULT
	/// instead of "the thing you picked no longer exists".
	/// </para>
	/// <para>
	/// The probe's Transient border leaves the tree and comes back on a cycle, announcing each
	/// departure in the event stream. It re-adds the same instance on purpose, which is what
	/// virtualization and a rebuilt panel do -- so the handle stays valid across the removal, and
	/// nothing about it can be used as a liveness test.
	/// </para>
	/// <para>
	/// Testable at all only because of #46: a click is a human action, and this suite cannot make one.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Clears_a_selection_whose_element_leaves_the_tree()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// Transient is only in the tree for part of its cycle, so finding it is a retry rather than
			// a single read. Selecting it is the same call, because a select on something with no
			// bounds is refused rather than half-applied.
			var selected = await SelectTransientAsync(session, cancellationToken);
			Assert.True(selected.Selected, $"expected to select Transient; got: {selected.Detail}");
			Assert.Equal("Transient", selected.Name);

			// Now wait for the app to take it away. The exception is the only channel out of the app.
			var removed = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpTransientRemovedException") ?? false),
				cancellationToken);
			Assert.NotNull(removed);

			// The provider clears on the removal callback, which arrives on the app's UI thread as the
			// removal happens -- so by the time the exception has been observed the work is done.
			var after = await session.ReadXamlSelectionAsync(cancellationToken);

			Assert.False(after.Selected, "an element leaving the tree clears the selection");
			Assert.Equal(0ul, after.Handle);
			Assert.Contains("removed from the visual tree", after.Detail ?? string.Empty, StringComparison.Ordinal);

		}
	}

	/// <summary>
	/// Selects the probe's Transient border, waiting for one of its in-tree phases. Absent is the
	/// expected answer some of the time, and a select against an element with no bounds is refused,
	/// so both are retried rather than treated as failures.
	/// </summary>
	private static async Task<LiveXamlSelection> SelectTransientAsync(
		LiveAppSession session,
		CancellationToken cancellationToken)
	{
		LiveXamlSelection last = new() { Detail = "Transient never appeared in the tree." };

		for (var attempt = 0; attempt < 20; attempt++)
		{
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			if (tree.Nodes.FirstOrDefault(node => node.Name == "Transient") is { } transient)
			{
				last = await session.SelectXamlElementAsync(transient.Handle, cancellationToken);
				if (last.Selected) return last;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
		}

		return last;
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
	/// A XAML request against a target this session is holding is refused, immediately and by name,
	/// rather than spending the endpoint's whole budget failing.
	/// </summary>
	/// <remarks>
	/// The endpoint is created by the target's own UI thread, and InitializeXamlDiagnosticsEx does not
	/// return until that thread has sited the tap -- so a stopped target cannot serve a XAML request at
	/// all. Before this was checked, the two bounds decided the outcome between them: the endpoint gets
	/// twenty seconds and a held target releases itself after thirty, so the request always expired
	/// first and then reported that the app was still starting or had no XAML UI. Both are false of an
	/// app that is stopped, and one of them is false of any app with a window.
	/// <para>
	/// It launches its own app rather than taking the shared one, because a target held at a breakpoint
	/// is app-global state with no smaller owner, and a test that failed between the stop and the
	/// resume would hand on an app that answers nothing.
	/// </para>
	/// </remarks>
	[Test]
	[ClassicOwnApp]
	public async Task Refuses_a_xaml_request_while_the_target_is_stopped()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp probe",
				},
				cancellationToken);

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			// The timer tick, which the probe runs forever, so the breakpoint is certain to be hit.
			var breakpoint = await session.SetBreakpointAsync(
				"Rose.ProbeApp.UwpClassic!Rose.ProbeApp.UwpClassic.MainPage.Tick",
				autoContinueSeconds: null,
				condition: null,
				cancellationToken);

			Assert.True(breakpoint.Bound, $"the breakpoint should bind against the loaded module; detail: {breakpoint.Detail}");

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);

			var refused = Stopwatch.StartNew();
			var whileStopped = await session.ReadXamlTreeAsync(cancellationToken);
			refused.Stop();

			Assert.Empty(whileStopped.Nodes);
			Assert.NotNull(whileStopped.Detail);
			Assert.Contains("stopped", whileStopped.Detail!);

			// The number that matters: refused rather than waited out. The endpoint's own bound is twenty
			// seconds, so anything in that region means the guard did not fire and the old failure is back.
			Assert.True(
				refused.Elapsed < TimeSpan.FromSeconds(5),
				$"expected an immediate refusal, not a wait for the endpoint; took {refused.Elapsed.TotalSeconds:0.0}s");

			// And the guard is not a one-way door: resumed, the same read works.
			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.ContinueAsync(cancellationToken));

			var afterResume = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(
				afterResume.Detail is null,
				$"expected a tree once the target was resumed, got detail: {afterResume.Detail}");
			Assert.NotEmpty(afterResume.Nodes);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			probe.StopApp();
		}
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
	/// held once the condition holds. The probe increments its argument each loop, so a condition on a
	/// value well beyond the count reached by attach time proves the earlier hits were skipped.
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

			// The probe reaches iteration 30 well after attach, so hits before it are gated out.
			var breakpoint = await session.SetBreakpointAsync(
				"DebugProbeTarget.Program.Beat", autoContinueSeconds: null, condition: "iteration == 30", cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");
			Assert.Equal("iteration == 30", breakpoint.Condition);

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);

			// It stopped at exactly the conditioned value, having skipped every earlier hit.
			var iteration = stop!.Variables!.First(variable => variable.Name == "iteration");
			Assert.Equal("30", iteration.Value);

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

	private static async Task<LiveDebugEvent?> WaitForEventAsync(
		LiveAppSession session,
		Func<LiveDebugEvent, bool> match,
		CancellationToken cancellationToken,
		long startCursor = 0)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		var cursor = startCursor;

		while (DateTime.UtcNow < deadline)
		{
			var page = await session.ReadEventsAsync(cursor, cancellationToken);
			var found = page.Events.FirstOrDefault(match);
			if (found is not null) return found;

			cursor = page.NextCursor;
			await Task.Delay(200, cancellationToken);
		}

		return null;
	}

	private static Process StartProbeTarget() => StartProcess(ProbeTargetPath());

	private static Process StartProcess(string path)
	{
		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(path),
		};

		return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}.");
	}

	/// <summary>
	/// Waits for a launched probe to have a window, and says exactly what happened when it does not.
	/// </summary>
	/// <remarks>
	/// Replaces a bare six-second sleep, which was wrong in both directions. It waited six seconds on
	/// a machine that was ready in one, and on a machine where the app died at startup it waited the
	/// same six and then attached to nothing -- so a WinUI probe that failed to bootstrap the Windows
	/// App Runtime under load presented as a test hanging or failing on an attach, with the actual
	/// cause two layers down and no message anywhere (#129).
	/// <para>
	/// An app that exits is a fact about this machine rather than about the change under test, so it
	/// skips with the exit code rather than failing. An app that is up but slow costs only the time it
	/// actually needs.
	/// </para>
	/// </remarks>
	private static async Task WaitForProbeWindowAsync(Process child, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

		while (DateTime.UtcNow < deadline)
		{
			if (child.HasExited)
			{
				Skip.Test(
					$"The probe app exited with code {child.ExitCode} before it could be attached to, which on WinUI "
						+ "is usually the Windows App Runtime failing to bootstrap.");
			}

			child.Refresh();

			if (child.MainWindowHandle != nint.Zero) return;

			await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
		}

		Skip.Test("The probe app did not open a window within 30 seconds.");
	}

	private static string ProbeTargetPath()
	{
		var exe = Path.Combine(RepositoryRoot(), "tests", "DebugProbeTarget", "bin", Configuration(), "net10.0", "DebugProbeTarget.exe");
		if (!File.Exists(exe)) throw new FileNotFoundException("The debug probe target was not built.", exe);

		return exe;
	}

	/// <summary>
	/// Launching a packaged app that is already running is not a launch: the system foregrounds the
	/// window that exists, no new process appears, and a from-birth debugger waits for a startup that
	/// will never happen. That surfaced as "the UWP resume stub did not connect; the app may not have
	/// activated under the debugger" -- a description of the symptom for a cause sitting in the process
	/// list all along.
	/// <para>
	/// It refuses rather than attaching, and names the pid. Attaching would silently hand back a
	/// mid-life session where a from-birth one was asked for, which is the entire reason to launch.
	/// </para>
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Refuses_to_launch_a_uwp_app_that_is_already_running()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: false, TestContext.Current!.Execution.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			// Started outside the debugger, the way a person would: shell:AppsFolder is how a packaged
			// app is activated without any debugging involvement at all.
			using (var launcher = Process.Start("explorer.exe", $"shell:AppsFolder\\{aumid}"))
			{
				launcher?.WaitForExit(10_000);
			}

			if (!await WaitForProbeProcessAsync(cancellationToken))
			{
				Skip.Test("The UWP probe app did not start outside the debugger.");
			}

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp already-running probe",
				},
				cancellationToken);

			var summary = session.Describe();
			Assert.Equal(LiveAppSessionState.Faulted, summary.State);
			Assert.Contains("already running", summary.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

			// The remedy is named, and it is the one that works.
			Assert.Contains("rose_debug_attach", summary.Detail ?? string.Empty, StringComparison.Ordinal);

			await manager.CloseAsync(session.SessionId, cancellationToken);
		}
		finally
		{
			foreach (var probe in Process.GetProcessesByName("Rose.ProbeApp.UwpClassic"))
			{
				try
				{
					probe.Kill();
				}
				catch (Exception)
				{
					// Already gone.
				}
				finally
				{
					probe.Dispose();
				}
			}

			probe.StopApp();
		}
	}

	private static async Task<bool> WaitForProbeProcessAsync(CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
		while (DateTime.UtcNow < deadline)
		{
			var running = Process.GetProcessesByName("Rose.ProbeApp.UwpClassic");
			foreach (var process in running)
			{
				process.Dispose();
			}

			if (running.Length > 0) return true;
			await Task.Delay(500, cancellationToken);
		}

		return false;
	}

	private static TargetArchitecture ExpectedArchitecture => RuntimeInformation.ProcessArchitecture switch
	{
		System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
		System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.Arm64,
		System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
		_ => TargetArchitecture.Unknown,
	};

	private static LiveAppSessionManager CreateManager(ILoggerFactory? logs = null) => new(
		Options.Create(new BrokerOptions()),
		logs ?? NullLoggerFactory.Instance,
		NullLogger<LiveAppSessionManager>.Instance);

	/// <summary>
	/// A logger factory that keeps every message, for the assertions that can only be made about which
	/// path the work took rather than about the answer it produced.
	/// <para>
	/// The XAML channel is the case in point: the pipe and the work folder return the same tree, so a
	/// test that asserts the tree passes whichever served it. What separates them is a sentence in the
	/// log.
	/// </para>
	/// </summary>
	private sealed class RecordingLoggerFactory : ILoggerFactory
	{
		private readonly List<string> _lines = [];

		public IReadOnlyList<string> Lines
		{
			get
			{
				lock (_lines) return [.. _lines];
			}
		}

		public void AddProvider(ILoggerProvider provider)
		{
		}

		public ILogger CreateLogger(string categoryName) => new Recorder(_lines);

		public void Dispose()
		{
		}

		private sealed class Recorder(List<string> lines) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state)
				where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				var line = formatter(state, exception);

				lock (lines) lines.Add(line);
			}
		}
	}
}
