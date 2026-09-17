using System.Diagnostics;

using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The classic UWP probe app as a debug target, and the channel into it: activating it by AUMID,
/// debugging it from its first managed instruction, reaching it over the tap's pipe from outside its
/// AppContainer, giving the framework interfaces back on detach, and refusing what cannot be served.
/// <para>
/// Mostly <c>[ClassicOwnApp]</c>, because a test about launching needs a process of its own and ends
/// the shared one to get it -- which is why those are admitted last, after everything that could have
/// used the app they take away. <see cref="ProbeKeys"/> is where that ordering is argued.
/// </para>
/// </summary>
[Category("LiveApp")]
[ClassDataSource<UwpProbeApp>(Shared = SharedType.PerAssembly)]
public sealed class LiveAppUwpTests(UwpProbeApp probe)
{
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
				Unavailable(probe.HasLaunched, "The UWP probe app did not start outside the debugger.");
			}

			probe.NoteLaunched();

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
}
