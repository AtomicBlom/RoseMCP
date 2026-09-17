using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Starting the plain .NET probe target, giving it a session manager, waiting for one of its debug
/// events, and the few pieces every live-app suite shares whatever it debugs: a logger that records
/// what the host said, a process start, and the one rule that decides whether a probe app which will
/// not come up is a skip or a failure.
/// <para>
/// Shared rather than copied into each test class, because the copies drift and each one drifts
/// silently. The probe's output path moves with the configuration and the target framework, so a
/// class holding its own spelling of it fails with a missing file and sends the reader to the build
/// rather than to the path.
/// </para>
/// </summary>
internal static class ProbeTargetSession
{
	/// <summary>
	/// A session manager with the default broker options, for tests that own their own rather than
	/// reaching one through a server process.
	/// </summary>
	/// <param name="logs">
	/// A factory to record what the session logged, for an assertion that can only be made about
	/// which path the work took rather than about the answer it produced.
	/// </param>
	internal static LiveAppSessionManager CreateManager(ILoggerFactory? logs = null) => new(
		Options.Create(new BrokerOptions()),
		logs ?? NullLoggerFactory.Instance,
		NullLogger<LiveAppSessionManager>.Instance);

	/// <summary>
	/// A dedicated child process to attach to, rather than this test runner: attaching a debugger to
	/// the process running the test perturbs it, and the probe does nothing but throw a distinctively
	/// named exception on a loop.
	/// </summary>
	internal static Process StartProbeTarget()
	{
		var path = ProbeTargetPath();
		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(path),
		};

		return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}.");
	}

	/// <summary>Where the probe target's build output is, refusing rather than starting nothing.</summary>
	internal static string ProbeTargetPath()
	{
		var exe = Path.Combine(RepositoryRoot(), "tests", "DebugProbeTarget", "bin", Configuration(), "net10.0", "DebugProbeTarget.exe");
		if (!File.Exists(exe)) throw new FileNotFoundException("The debug probe target was not built.", exe);

		return exe;
	}

	/// <summary>
	/// Reads the session's event stream until one matches, or thirty seconds pass. Null is the
	/// timeout, which every caller asserts on rather than being thrown at, so the failure names the
	/// event that never arrived.
	/// </summary>
	internal static async Task<LiveDebugEvent?> WaitForEventAsync(
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

	internal static Process StartProcess(string path)
	{
		var start = new ProcessStartInfo(path)
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(path),
		};

		return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {path}.");
	}


	/// <summary>
	/// A logger factory that keeps every message, for the assertions that can only be made about which
	/// path the work took rather than about the answer it produced.
	/// <para>
	/// The XAML channel is the case in point: the pipe and the work folder return the same tree, so a
	/// test that asserts the tree passes whichever served it. What separates them is a sentence in the
	/// log.
	/// </para>
	/// </summary>
	internal sealed class RecordingLoggerFactory : ILoggerFactory
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

	/// <summary>
	/// Skips or fails, on the one question that separates the two: has this app ever come up in this
	/// run?
	/// <para>
	/// A machine that cannot run these tests never produces a first success and goes on skipping,
	/// which is what keeps a laptop without the WinUI tooling, or one where the Windows App Runtime
	/// never bootstraps (#180), out of the red. A run that produced a first success and then could not
	/// is reporting something real, and a skip there is an acceptance test reading as green while it
	/// did not run.
	/// </para>
	/// </summary>
	/// <param name="hasLaunched">Whether the fixture has seen its app come up in this run.</param>
	/// <param name="reason">What happened, said the same way either side of the rule.</param>
	[DoesNotReturn]
	internal static void Unavailable(bool hasLaunched, string reason)
	{
		if (hasLaunched)
		{
			Assert.Fail($"{reason} It came up earlier in this run, so this is a failure rather than a limit of this machine.");
		}

		Skip.Test(reason);

		// Skip.Test throws, and the compiler cannot know that from an attribute the framework does not
		// carry. Marking this method as not returning is what lets the callers read as guards.
		throw new InvalidOperationException(reason);
	}

	/// <summary>An attach-by-pid target for the probe, which is how most of these sessions start.</summary>
	internal static LiveAppTarget AttachTo(int processId) => new()
	{
		Kind = LiveAppTargetKind.AttachProcess,
		ProcessId = processId,
		Description = "probe target",
	};
}
