using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Starting the plain .NET probe target, giving it a session manager, and waiting for one of its
/// debug events: the three things every test that debugs something other than a XAML app needs.
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
}
