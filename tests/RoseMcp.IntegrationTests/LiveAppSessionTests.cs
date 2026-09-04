using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Integration tests for the live-app debug session. Like the broker tests, these spawn a real host
/// process, because attach, supervision, and reclaiming are properties of process lifetime -- and the
/// host attaches a real ICorDebug session to a real .NET process.
/// </summary>
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

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
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

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
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp xaml probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			var summary = session.Describe();
			Assert.True(
				summary.State == LiveAppSessionState.Ready,
				$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

			// Wait until the app is well into running (its timer has ticked once) so the window and its
			// visual tree are up before we enumerate.
			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

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

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp properties probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

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
			Assert.Equal(9, captionProperties.SourceLine);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp corner-radius probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var pane = tree.Nodes.FirstOrDefault(node => node.Name == "Pane");
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
			var all = await session.ReadXamlPropertiesAsync(tree.Nodes[0].Handle, includeDefaults: true, cancellationToken);
			var unavailable = all.Properties.Where(property => property.ValueUnavailable).ToArray();

			Assert.All(unavailable, property => Assert.Equal(string.Empty, property.Value));

			// And a string that is genuinely empty is not flagged, or the flag fires on the majority of
			// empty values and stops meaning anything.
			Assert.DoesNotContain(
				all.Properties.Where(property => property.ValueType == "Windows.Foundation.String"),
				property => property.ValueUnavailable);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

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
				Path.GetFullPath(layout).TrimEnd(Path.DirectorySeparatorChar),
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
			UnregisterUwpProbeApp();
		}
	}

	/// <summary>
	/// XAML hot reload (#12): diff two versions of the probe's XAML and apply the changes to the live
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
	public async Task Hot_reloads_a_property_on_the_live_uwp_probe()
	{
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);
		var newXaml = oldXaml
			.Replace("FontSize=\"24\"", "FontSize=\"40\"")
			.Replace("CornerRadius=\"8\"", "CornerRadius=\"0\"");
		Assert.DoesNotContain("FontSize=\"40\"", oldXaml);
		Assert.DoesNotContain("CornerRadius=\"0\"", oldXaml);

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp hot-reload probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			var reload = await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
			Assert.True(reload.Detail is null, $"expected a reload, got detail: {reload.Detail}");

			var edit = reload.Results.FirstOrDefault(result => result.Target == "#Caption" && result.Property == "FontSize");
			Assert.NotNull(edit);
			Assert.Equal("applied", edit!.Status);

			// The struct-valued edit, which is the one that used to come back "SetProperty failed
			// 0x80004005" because it had been built as a Double.
			var radius = reload.Results.FirstOrDefault(result => result.Target == "#Pane" && result.Property == "CornerRadius");
			Assert.NotNull(radius);
			Assert.Equal("applied", radius!.Status);

			Assert.Equal(2, reload.Applied);

			// The live element actually changed: reading its font size back gives the new value.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var caption = tree.Nodes.First(node => node.Name == "Caption");
			var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
			var fontSize = properties.Properties.FirstOrDefault(property => property.Name == "FontSize");
			Assert.NotNull(fontSize);
			Assert.Equal("40", fontSize!.Value);

			// And the struct-valued edit is now read back too, rather than trusted from its status.
			// It used to be asserted only through "applied", because a CornerRadius came back as an
			// empty string -- which is #21, and is fixed, so the weaker assertion has no reason left.
			var pane = tree.Nodes.First(node => node.Name == "Pane");
			var paneProperties = await session.ReadXamlPropertiesAsync(pane.Handle, includeDefaults: false, cancellationToken);
			var cornerRadius = paneProperties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");
			Assert.NotNull(cornerRadius);
			Assert.Equal("0,0,0,0", cornerRadius!.Value);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
	public async Task Hot_reloads_an_unnamed_element_by_its_address()
	{
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		// The second of the two unnamed Borders, and nothing else in the file.
		const string FirstBackground = "#FF3A2A2A";
		const string SecondBackground = "#FF2A3A2A";
		const string ChangedBackground = "#FF00FF00";
		Assert.Contains(SecondBackground, oldXaml);
		var newXaml = oldXaml.Replace(SecondBackground, ChangedBackground);

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp address probe",
				},
				cancellationToken);

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			// The provider derives an address from the live tree, so this half stands on its own: two
			// unnamed siblings of one type are told apart by their position under the named anchor.
			var before = await session.ReadXamlTreeAsync(cancellationToken);
			var addresses = before.Nodes.Select(node => node.Address).ToList();
			Assert.Contains("#Pair/Border[0]", addresses);
			Assert.Contains("#Pair/Border[1]", addresses);

			var reload = await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
			Assert.True(reload.Detail is null, $"expected a reload, got detail: {reload.Detail}");

			var edit = reload.Results.FirstOrDefault(result => result.Target == "#Pair/Border[1]" && result.Property == "Background");
			Assert.NotNull(edit);
			Assert.Equal("applied", edit!.Status);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Equal(ChangedBackground, await BackgroundAtAsync(session, tree, "#Pair/Border[1]", cancellationToken));
			Assert.Equal(FirstBackground, await BackgroundAtAsync(session, tree, "#Pair/Border[0]", cancellationToken));

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		// Cut the second of the two unnamed Borders out of the markup, found by its colour rather than
		// by a copied block of text so that reindenting the fixture cannot quietly stop this matching.
		const string SecondBorder = "<Border Background=\"#FF2A3A2A\"";
		const string FirstBackground = "#FF3A2A2A";
		var start = oldXaml.IndexOf(SecondBorder, StringComparison.Ordinal);
		Assert.True(start >= 0, "the probe markup no longer holds the second unnamed Border");
		var close = oldXaml.IndexOf("</Border>", start, StringComparison.Ordinal) + "</Border>".Length;
		var newXaml = oldXaml.Remove(start, close - start);

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp removal probe",
				},
				cancellationToken);

			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

			var before = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.Equal(2, before.Nodes.Count(node => node.Address is "#Pair/Border[0]" or "#Pair/Border[1]"));

			var reload = await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
			Assert.True(reload.Detail is null, $"expected a reload, got detail: {reload.Detail}");

			var removal = reload.Results.FirstOrDefault(result => result.Kind == "RemoveChild");
			Assert.NotNull(removal);
			Assert.Equal("#Pair/Border[1]", removal!.Target);
			Assert.Equal("applied", removal.Status);

			// Read back off a fresh enumeration, so this checks the app rather than the provider's own
			// bookkeeping: every injection builds a new tap and walks the tree again.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var remaining = tree.Nodes.Where(node => node.Address is "#Pair/Border[0]" or "#Pair/Border[1]").ToList();
			var survivor = Assert.Single(remaining);
			Assert.Equal("#Pair/Border[0]", survivor.Address);

			// And it is the one that was meant to stay.
			Assert.Equal(FirstBackground, await BackgroundAtAsync(session, tree, "#Pair/Border[0]", cancellationToken));

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

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

			var reload = await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
			Assert.True(reload.Detail is null, $"expected a reload, got detail: {reload.Detail}");

			var add = reload.Results.FirstOrDefault(result => result.Kind == "AddChild");
			Assert.NotNull(add);
			Assert.Equal("applied", add!.Status);

			var removal = reload.Results.FirstOrDefault(result => result.Kind == "RemoveChild");
			Assert.NotNull(removal);
			Assert.Equal("applied", removal!.Status);

			// The non-brush property, on a named element, so all three kinds are in the one apply.
			var opacity = reload.Results.FirstOrDefault(result => result.Property == "Opacity");
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
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

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

			var reload = await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
			Assert.True(reload.Detail is null, $"expected a reload, got detail: {reload.Detail}");

			var edit = reload.Results.FirstOrDefault(result => result.Property == "Grid.Row");
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
			UnregisterUwpProbeApp();
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
	/// Interactive selection (#18): every XAML tool leaves RoseMCP's toolbar resident on the app's
	/// diagnostics UI layer, the tree snapshot keeps that toolbar out of its answer, and arming select
	/// mode is reported by the provider rather than assumed here. Until someone clicks, the selection is
	/// empty rather than stale or invented -- the click is a human action, so this stops at the armed
	/// state rather than driving the mouse on a live desktop.
	/// </summary>
	[Fact]
	public async Task Arms_interactive_select_mode_on_the_classic_uwp_probe()
	{
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp select probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

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

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp select-by-handle probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			Assert.NotNull(running);

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

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
		}
	}

	/// <summary>
	/// A handle that names something real but not an element -- a Brush has one too -- is refused with
	/// a sentence rather than drawing an outline round nothing.
	/// </summary>
	[Fact]
	public async Task Refuses_to_select_a_handle_that_is_not_an_element()
	{
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp bad-handle probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);

			await session.ReadXamlTreeAsync(cancellationToken);

			// A handle nothing owns. The provider resolves it, finds nothing, and declines.
			var selected = await session.SelectXamlElementAsync(1, cancellationToken);

			Assert.False(selected.Selected);
			Assert.NotNull(selected.Detail);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");
		if (!BuildXamlProvider()) Assert.Skip("The native XAML provider could not be built (no C++ toolset).");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp removal probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);

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

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			UnregisterUwpProbeApp();
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

	// The x64 host and target are built on demand for the architecture-shim test, since a normal build
	// produces only the broker's own RID. On an x64 machine this is a same-arch build; on ARM it is the
	// emulated-x64 case classic UWP needs.
	private static void EnsureX64HostBuilt() => EnsureX64Build("src", "RoseMcp.LiveApp", "net10.0-windows", "RoseMcp.LiveApp.exe");

	private static string EnsureX64ProbeTargetBuilt() => EnsureX64Build("tests", "DebugProbeTarget", "net10.0", "DebugProbeTarget.exe");

	// Built once per test run, never merely "found". Skipping the build when the exe already exists is
	// the obvious optimisation and it is wrong: the win-x64 output is a separate RID build that a normal
	// `dotnet build` of the solution does not touch, so an existing exe is routinely one source change
	// out of date -- and the test then exercises yesterday's host and reports a failure that is not
	// there. MSBuild is incremental, so paying for the check once a run costs almost nothing.
	private static readonly Dictionary<string, string> X64Builds = [];

	private static string EnsureX64Build(string area, string project, string targetFramework, string exeName)
	{
		var root = RepositoryRoot();
		var configuration = Configuration();
		var exe = Path.Combine(root, area, project, "bin", configuration, targetFramework, "win-x64", exeName);

		lock (X64Builds)
		{
			if (X64Builds.TryGetValue(exe, out var built)) return built;

			var csproj = Path.Combine(root, area, project, $"{project}.csproj");
			RunDotnet($"build \"{csproj}\" -r win-x64 -c {configuration} --nologo");

			if (!File.Exists(exe)) throw new FileNotFoundException($"The win-x64 build did not produce {exeName}.", exe);
			X64Builds[exe] = exe;
			return exe;
		}
	}

	private static void RunDotnet(string arguments)
	{
		var (exitCode, output) = RunProcess("dotnet", arguments);
		if (exitCode != 0) throw new InvalidOperationException($"dotnet {arguments} failed:{Environment.NewLine}{output}");
	}

	private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
	{
		var start = new ProcessStartInfo(fileName, arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		return (process.ExitCode, output);
	}

	/// <summary>
	/// The MSBuild that can build classic UWP, found via vswhere, or null when no such Visual Studio is
	/// present -- in which case the UWP test skips rather than fails.
	/// </summary>

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
		var msbuild = FindUwpMsBuild();
		if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");

		EnsureX64HostBuilt();

		var layout = BuildUwpProbeApp(msbuild!);
		var aumid = RegisterUwpProbeApp(layout);
		if (aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");

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

			UnregisterUwpProbeApp();
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

	private static string? FindUwpMsBuild()
	{
		var vswhere = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			"Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!File.Exists(vswhere)) return null;

		var (exitCode, output) = RunProcess(
			vswhere,
			"-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe");
		if (exitCode != 0) return null;

		var msbuild = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase));
		if (msbuild is null || !File.Exists(msbuild)) return null;

		// MSBuild alone is not enough; the classic-UWP C# targets must be installed too.
		var windowsXaml = Path.Combine(Path.GetDirectoryName(msbuild)!, "..", "..", "..", "MSBuild", "Microsoft", "WindowsXaml");
		return Directory.Exists(Path.GetFullPath(windowsXaml)) ? msbuild : null;
	}

	private static string UwpProbeAppDirectory()
		=> Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic");

	/// <summary>
	/// Builds the classic UWP probe app Debug|x64 and returns the deployable AppX layout, staged the
	/// way Visual Studio's deploy stages it.
	/// </summary>
	private static string BuildUwpProbeApp(string msbuild)
	{
		var csproj = Path.Combine(UwpProbeAppDirectory(), "Rose.ProbeApp.UwpClassic.csproj");

		var restore = RunProcess(msbuild, $"\"{csproj}\" -t:Restore -p:Configuration=Debug -p:Platform=x64 -v:minimal -nologo");
		if (restore.ExitCode != 0) throw new InvalidOperationException($"UWP restore failed:{Environment.NewLine}{restore.Output}");

		var build = RunProcess(msbuild, $"\"{csproj}\" -t:Build -p:Configuration=Debug -p:Platform=x64 -v:minimal -nologo");
		if (build.ExitCode != 0) throw new InvalidOperationException($"UWP build failed:{Environment.NewLine}{build.Output}");

		var buildOutput = Path.Combine(UwpProbeAppDirectory(), "bin", "x64", "Debug");
		return StageUwpProbeLayout(buildOutput);
	}

	/// <summary>
	/// Stages the deployable AppX layout the way Visual Studio's deploy does, from the
	/// .build.appxrecipe MSBuild emits -- because a straight register of the build folder does not
	/// produce a runnable app. A classic-UWP CoreCLR debug build makes two executables: the managed
	/// app assembly, and a native CoreCLR apphost under Core\. Only the recipe's layout wires them
	/// correctly -- the native apphost becomes the package executable, the managed assembly moves under
	/// entrypoint\, and the CoreCLR System.Runtime.dll (not the desktop-framework one that also sits in
	/// the build folder) is placed beside them. Register the root manifest instead and Windows hosts
	/// the managed exe under the desktop .NET Framework CLR, which cannot load CoreCLR's
	/// System.Private.CoreLib and dies with a BadImageFormatException at host init, before any app code
	/// runs. MSBuild's Build target emits the recipe but does not stage the layout (these old-style
	/// projects have no Deploy target), so the staging is done here.
	/// </summary>
	private static string StageUwpProbeLayout(string buildOutputDirectory)
	{
		var recipePath = Path.Combine(buildOutputDirectory, "Rose.ProbeApp.UwpClassic.build.appxrecipe");
		if (!File.Exists(recipePath)) throw new InvalidOperationException($"No appxrecipe at {recipePath}; the UWP build did not complete.");

		XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
		var recipe = XDocument.Load(recipePath);

		var layoutText = recipe.Descendants(ns + "LayoutDir").FirstOrDefault()?.Value
			?? throw new InvalidOperationException("The appxrecipe declares no LayoutDir.");
		var layoutDirectory = Uri.UnescapeDataString(layoutText);

		if (Directory.Exists(layoutDirectory)) Directory.Delete(layoutDirectory, recursive: true);
		Directory.CreateDirectory(layoutDirectory);

		// Both the manifest and every packaged file carry an Include (the source on disk, MSBuild-escaped)
		// and a PackagePath (where it lands in the layout).
		var entries = recipe.Descendants(ns + "AppXManifest").Concat(recipe.Descendants(ns + "AppxPackagedFile"));
		foreach (var entry in entries)
		{
			var source = Uri.UnescapeDataString(entry.Attribute("Include")!.Value);
			var packagePath = Uri.UnescapeDataString(entry.Element(ns + "PackagePath")!.Value);
			var destination = Path.Combine(layoutDirectory, packagePath);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(source, destination, overwrite: true);
		}

		return layoutDirectory;
	}

	/// <summary>
	/// Builds the native XAML diagnostics provider (x64) with build.ps1. Returns false only when the
	/// toolchain is genuinely absent, so the caller skips; anything else throws.
	/// <para>
	/// That distinction is the point. This used to return false for any non-zero exit and the caller
	/// skipped with the message "no C++ toolset", which meant a compile error in the provider -- or
	/// two builds racing over one PDB, which is how it was noticed -- silently skipped the XAML tests
	/// and left the suite green. A capability quietly not being tested is worse than a red build, and
	/// looks identical to a machine that simply cannot build it. build.ps1 already separates the two:
	/// it exits 3 from its own Fail for a missing toolset or SDK, and anything else is a real failure.
	/// </para>
	/// </summary>
	private static bool BuildXamlProvider()
	{
		var script = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.Uwp.Tap", "build.ps1");
		if (!File.Exists(script)) return false;

		var (exitCode, output) = RunProcess(
			"powershell",
			$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Platform x64 -Configuration Debug");

		// 3 is build.ps1's Fail: no MSVC toolset, or no Windows SDK. The only skippable outcome.
		if (exitCode == 3) return false;

		if (exitCode != 0)
		{
			throw new InvalidOperationException(
				$"Building the XAML provider failed (exit {exitCode}):{Environment.NewLine}{output}");
		}

		var dll = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.Uwp.Tap", "bin", "x64", "Debug", "RoseMcp.Xaml.Uwp.Tap.dll");
		if (!File.Exists(dll))
		{
			throw new InvalidOperationException($"The XAML provider build reported success but produced no {dll}.");
		}

		return true;
	}

	private const string UwpProbePackageName = "RoseMcp.ProbeApp.UwpClassic";

	/// <summary>
	/// Registers the loose UWP layout and returns its AUMID, or null when registration is not permitted
	/// (developer mode off), so the test can skip rather than fail on an environment limit.
	/// </summary>
	private static string? RegisterUwpProbeApp(string layoutDirectory)
	{
		var manifest = Path.Combine(layoutDirectory, "AppxManifest.xml");
		var script =
			$"try {{ Add-AppxPackage -Register '{manifest}' -ErrorAction Stop }} catch {{ Write-Output ('ERROR: ' + $_.Exception.Message); exit 0 }}; "
				+ $"$p = Get-AppxPackage '{UwpProbePackageName}'; if ($p) {{ Write-Output ('PFN: ' + $p.PackageFamilyName) }}";
		var (_, output) = RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");

		var pfnLine = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("PFN: ", StringComparison.Ordinal));
		if (pfnLine is null) return null;

		return $"{pfnLine["PFN: ".Length..].Trim()}!App";
	}

	private static void UnregisterUwpProbeApp()
	{
		RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"Get-AppxPackage '{UwpProbePackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"");
	}

	private static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");
	}

	private static string Configuration()
		=> AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
			? "Release"
			: "Debug";

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
