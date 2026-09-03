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

			var remaining = await session.RemoveTracepointAsync(tracepoint.Id, cancellationToken);
			Assert.Empty(remaining);

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

	private static string EnsureX64Build(string area, string project, string targetFramework, string exeName)
	{
		var root = RepositoryRoot();
		var configuration = Configuration();
		var exe = Path.Combine(root, area, project, "bin", configuration, targetFramework, "win-x64", exeName);
		if (File.Exists(exe)) return exe;

		var csproj = Path.Combine(root, area, project, $"{project}.csproj");
		RunDotnet($"build \"{csproj}\" -r win-x64 -c {configuration} --nologo");

		if (!File.Exists(exe)) throw new FileNotFoundException($"The win-x64 build did not produce {exeName}.", exe);
		return exe;
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
