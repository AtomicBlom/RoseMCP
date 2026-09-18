using System.Diagnostics;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Ending a packaged probe app, and not returning until it is actually gone.
/// <para>
/// Shared because every fixture that has needed it has needed the same thing and got it wrong
/// differently. A packaged app is single-instance: activating one that is already running
/// foregrounds the existing window rather than starting a process, so a test that asked for a
/// from-birth debugger waits for a startup that never comes and reports an app already running.
/// Killing a process and the package being launchable again are not the same moment.
/// </para>
/// <para>
/// Two things have to be true for a stop to have worked, and each has been missed on its own. The
/// wait has to be on the process list rather than on the handles one pass of it returned, because a
/// process that appeared after the enumeration was never in that set. And the wait has to be able to
/// fail: a bounded wait that expires and returns anyway reports success for a machine it has just
/// failed to clean, and the test that pays for it is the next one.
/// </para>
/// </summary>
internal static class ProbeAppProcess
{
	/// <summary>
	/// How long a stop is waited for. Generous because the bound only binds under load, which is
	/// exactly when the waiting is needed and when a tight one would fail a machine that is busy
	/// rather than broken.
	/// </summary>
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

	/// <summary>Whether any instance of <paramref name="processName"/> is running right now.</summary>
	internal static bool IsRunning(string processName)
	{
		var running = Process.GetProcessesByName(processName);
		foreach (var process in running) process.Dispose();

		return running.Length > 0;
	}

	/// <summary>
	/// Ends every instance and waits for the process list to come back empty, reporting whether it
	/// did. Best effort by design: this is what a teardown calls, and a teardown that throws replaces
	/// the failure its test was about to report with one about cleaning up after it.
	/// </summary>
	internal static bool Stop(string processName)
	{
		var waiting = Stopwatch.StartNew();

		while (true)
		{
			var running = Process.GetProcessesByName(processName);
			if (running.Length == 0) return true;

			foreach (var process in running)
			{
				try
				{
					if (!process.HasExited) process.Kill(entireProcessTree: true);
				}
				catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					// It exited between the enumeration and the kill, or it is already going. The next
					// pass decides whether it is gone, rather than this call.
				}
				finally
				{
					process.Dispose();
				}
			}

			if (waiting.Elapsed >= StopTimeout) return false;

			Thread.Sleep(PollInterval);
		}
	}

	/// <summary>
	/// Ends every instance and refuses to continue if any survives. What a test calls as it takes the
	/// app, so that a turn establishes its own precondition rather than trusting the last one to have
	/// left the machine clean -- and so that a stop that cannot be made to work is reported against
	/// the run that could not clean up rather than against whichever test inherits the app.
	/// </summary>
	internal static void StopOrThrow(string processName)
	{
		if (Stop(processName)) return;

		throw new InvalidOperationException(
			$"{processName} was still running {StopTimeout.TotalSeconds:0}s after being asked to stop, so this test "
				+ "cannot have the app to itself. Launching by AUMID now would foreground the instance that is "
				+ "already there instead of starting one under a debugger.");
	}
}
