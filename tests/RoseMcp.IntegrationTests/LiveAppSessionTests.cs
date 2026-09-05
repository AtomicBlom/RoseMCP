using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

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
public sealed class LiveAppSessionTests(UwpProbeApp probe, WinUiProbeApp winui)
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
	[Fact]
	public async Task Attaches_to_a_dotnet_process_and_captures_an_exception()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Closing_a_session_detaches_cleanly_and_leaves_the_target_running()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));

			Assert.Null(session.DetachFailure);
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Closing_a_session_whose_target_has_already_exited_is_not_a_detach_failure()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Tracepoint_binds_logs_hits_and_can_be_removed()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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

			var hitsOnly = await session.ReadEventsAsync(0, "BreakpointHit", limit: 500, cancellationToken);
			Assert.NotEmpty(hitsOnly.Events);
			Assert.All(hitsOnly.Events, entry => Assert.Equal(LiveDebugEventKind.BreakpointHit, entry.Kind));

			// The two things that make a filter usable rather than a trap: it says how much it passed
			// over, and its cursor has moved past that -- so paging with it does not re-read forever.
			Assert.True(hitsOnly.Skipped > 0, "the filter should report the events it passed over");
			Assert.Equal(unfiltered.NextCursor, hitsOnly.NextCursor);

			// An unrecognised kind narrows to nothing rather than silently widening to everything.
			var nonsense = await session.ReadEventsAsync(0, "NotAKind", limit: 500, cancellationToken);
			Assert.Equal(unfiltered.Events.Count, nonsense.Events.Count);

			var remaining = await session.RemoveTracepointAsync(tracepoint.Id, cancellationToken);
			Assert.Empty(remaining.Tracepoints);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Launches_a_dotnet_process_under_the_debugger_and_captures_startup()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
	/// The architecture shim (issue #1): a target running as a different architecture than the broker
	/// is attached to by a host launched to match it. On this Windows-on-ARM box the target is x64 under
	/// emulation while the broker is ARM64, which is the exact case classic UWP needs; on an x64 machine
	/// it is a same-architecture x64 attach. Skips where the x64 .NET runtime is unavailable.
	/// </summary>
	[Fact]
	public async Task Attaches_to_a_target_of_a_different_architecture()
	{
		EnsureX64HostBuilt();
		var x64Target = EnsureX64ProbeTargetBuilt();

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

		using var child = StartProcess(x64Target);
		try
		{
			await Task.Delay(1500, cancellationToken);
			if (child.HasExited)
			{
				Assert.Skip($"The x64 probe target exited (code {child.ExitCode}); the x64 .NET runtime is not available here.");
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
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Launches_and_debugs_the_classic_uwp_probe_app()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: false, TestContext.Current.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
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
	[Fact]
	public async Task Captures_the_classic_uwp_probe_apps_startup_from_birth()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: false, TestContext.Current.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
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
	[Fact]
	public async Task Launches_and_debugs_the_unpackaged_winui_probe_app()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: false, cancellationToken);

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
	[Fact]
	public async Task Launches_and_debugs_the_packaged_winui_probe_app()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var turn = await winui.TakeAsync(packaged: true, needsXamlProvider: false, cancellationToken);

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
	[Fact]
	public async Task Reads_the_xaml_tree_of_a_winui_app_it_launched()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

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
	[Fact]
	public async Task Reads_the_xaml_tree_of_a_winui_app_it_attached_to()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		// Started outside the session on purpose: nothing about this process was arranged for us.
		using var child = StartProcess(turn.ExecutablePath);
		await using var manager = CreateManager();

		try
		{
			// Long enough that the window and its tree are up before anything attaches.
			await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);

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
	/// The XAML track's first vertical (#2/#3, seed of #9): launch the classic UWP probe, inject the
	/// diagnostics provider, and read its live visual tree. Proves the provider builds, injects into the
	/// AppContainer, enumerates on the UI thread, and reports the tree back through the host to the
	/// broker. Skips where the UWP build toolchain or the C++ toolset is absent.
	/// </summary>
	[Fact]
	public async Task Reads_the_live_visual_tree_of_the_classic_uwp_probe()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
			var firstPage = await session.ReadXamlTreeAsync(rootName: null, offset: 0, limit: 2, cancellationToken);
			Assert.Equal(2, firstPage.Nodes.Count);
			Assert.True(firstPage.Total > 2, $"expected more than a page of nodes; total {firstPage.Total}");
		}
	}

	/// <summary>
	/// XAML property inspection (#10): read an element's properties with provenance. Confirms the set
	/// (non-default) properties come back with a Local provenance for what the XAML sets -- including a
	/// concrete string value -- that framework defaults are filtered out unless asked for, and that it
	/// all rides through the host to the broker. Skips where the UWP or C++ toolchain is absent.
	/// </summary>
	[Fact]
	public async Task Reads_the_properties_of_a_xaml_element()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Reads_a_corner_radius_the_framework_renders_as_nothing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(radius.ValueUnavailable);

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
	[Fact]
	public async Task Names_the_install_location_a_uwp_session_activated()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: true, TestContext.Current.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
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
	[Fact]
	public async Task Live_edits_a_property_on_the_uwp_probe()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Live_edits_an_unnamed_element_by_its_address()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Removes_an_element_from_the_live_tree()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Adds_removes_and_retypes_in_one_apply()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current.CancellationToken);
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
		var cancellationToken = TestContext.Current.CancellationToken;
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
	[Fact]
	public async Task Sets_an_attached_property_on_the_live_tree()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current.CancellationToken);
		var aumid = lease.Aumid;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		Assert.Contains("Grid.Row=\"0\"", oldXaml);
		var newXaml = oldXaml.Replace("Grid.Row=\"0\"", "Grid.Row=\"1\"");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
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
	[Fact]
	public async Task Replaces_a_keyed_resource_on_the_live_tree()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Applies_successive_file_edits_to_the_running_app()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current.CancellationToken);
		var aumid = lease.Aumid;

		var sourcePath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var original = File.ReadAllText(sourcePath);
		var editable = Path.Combine(Path.GetTempPath(), $"rose-reload-{Guid.NewGuid():N}.xaml");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
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
	[Fact]
	public async Task Serves_two_xaml_calls_in_flight_together()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task A_second_properties_read_reports_what_reading_the_first_created()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
	[Fact]
	public async Task Arms_interactive_select_mode_on_the_classic_uwp_probe()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(withoutFilter.JustMyXaml);

			// And the toolbar agrees, because it is one switch rather than two pieces of state.
			var afterDisabling = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(afterDisabling.JustMyXaml);

			// Nothing picked yet: an empty selection that says so, safe to poll. Armed comes back from
			// the toolbar's own state file, so this is the provider reporting, not the host remembering.
			var selection = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(selection.Selected);
			Assert.True(selection.Armed, $"expected the toolbar to report select mode armed; got: {selection.Detail}");
			Assert.NotNull(selection.Detail);

			// #45: the pick can be cleared, and clearing says which of "cleared" and "there was nothing
			// selected" happened rather than treating both as success. Nothing has been picked here --
			// a click is a human action and this suite does not drive the mouse on a live desktop -- so
			// the second is the honest answer, and it is the one that used to be unreachable at all.
			var cleared = await session.ClearXamlSelectionAsync(cancellationToken);
			Assert.False(cleared.Selected);
			Assert.Contains("nothing", cleared.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

			// And the toolbar is still there afterwards, because deselecting is not leaving.
			var afterClearing = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(afterClearing.Selected);

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
	[Fact]
	public async Task Selects_a_xaml_element_by_handle_without_a_click()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(cleared.Selected);
			Assert.Contains("cleared", cleared.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

		}
	}

	/// <summary>
	/// A handle that names something real but not an element -- a Brush has one too -- is refused with
	/// a sentence rather than drawing an outline round nothing.
	/// </summary>
	[Fact]
	public async Task Refuses_to_select_a_handle_that_is_not_an_element()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			await session.ReadXamlTreeAsync(cancellationToken);

			// A handle nothing owns. The provider resolves it, finds nothing, and declines.
			var selected = await session.SelectXamlElementAsync(1, cancellationToken);

			Assert.False(selected.Selected);
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
	[Fact]
	public async Task Clears_a_selection_whose_element_leaves_the_tree()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

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

			Assert.False(after.Selected);
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
	[Fact]
	public async Task Reports_a_missing_target_as_faulted()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
	/// A stopping breakpoint (issue #6): set at a method by name, it holds the target on hit and
	/// records the stop with its stack; continuing resumes it, and detach leaves it running. This is
	/// the interactive counterpart to a tracepoint.
	/// </summary>
	[Fact]
	public async Task Stopping_breakpoint_holds_the_target_and_continue_resumes_it()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Conditional_breakpoint_stops_only_when_the_condition_holds()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Evaluates_a_field_access_expression_at_a_stop()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Step_from_a_stop_lands_a_step_complete_with_a_stack()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

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
			Assert.False(child.HasExited);
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
	[Fact]
	public async Task Refuses_to_launch_a_uwp_app_that_is_already_running()
	{
		await using var turn = await probe.TakeAppAsync(needsXamlProvider: false, TestContext.Current.CancellationToken);
		var aumid = turn.Aumid;

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
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
				Assert.Skip("The UWP probe app did not start outside the debugger.");
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

	private static LiveAppSessionManager CreateManager() => new(
		Options.Create(new BrokerOptions()),
		NullLoggerFactory.Instance,
		NullLogger<LiveAppSessionManager>.Instance);
}
