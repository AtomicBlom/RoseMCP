using System.Diagnostics;
using System.Runtime.InteropServices;

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
				"DebugProbeTarget.Program.Beat", "beat", logEveryNthHit: null, cancellationToken);
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

			var breakpoint = await session.SetBreakpointAsync("DebugProbeTarget.Program.Beat", autoContinueSeconds: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind against the loaded module; detail: {breakpoint.Detail}");

			// The hit holds the target and records the stop with a stack that names the method.
			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);
			Assert.NotNull(stop!.Frames);
			Assert.Contains(stop.Frames!, frame => frame.Contains("DebugProbeTarget.Program.Beat"));

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

	private static Process StartProbeTarget()
	{
		var path = ProbeTargetPath();
		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(path),
		};

		return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}.");
	}

	private static string ProbeTargetPath()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) throw new InvalidOperationException("Could not locate the repository root from the test binary.");

		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
			? "Release"
			: "Debug";

		var exe = Path.Combine(directory.FullName, "tests", "DebugProbeTarget", "bin", configuration, "net10.0", "DebugProbeTarget.exe");
		if (!File.Exists(exe)) throw new FileNotFoundException("The debug probe target was not built.", exe);

		return exe;
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
