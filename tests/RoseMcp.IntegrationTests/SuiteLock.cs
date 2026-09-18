using System.Text;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// One integration suite per machine, enforced before the first test runs.
/// <para>
/// This suite is not isolated by its checkout. It registers appx packages under a fixed package
/// family name, deploys and activates them, attaches debuggers to them, and binds ports -- all of
/// which are machine-wide. Two suites therefore fight even from different worktrees: one
/// deregisters the package the other is mid-way through launching, and what comes out is a spray of
/// failures that look like product bugs and are not. The cost of diagnosing one of those is an
/// afternoon; the cost of refusing to start is a sentence.
/// </para>
/// <para>
/// It lives beside the settings and the logs rather than in the repository, because the repository
/// is exactly the wrong scope: a lock under one checkout is invisible to the worktree that would
/// collide with it.
/// </para>
/// <para>
/// The file is held open for the length of the run rather than written and deleted. A handle is
/// released by the operating system when the process dies however it dies, so a killed runner
/// leaves nothing behind to clear by hand -- which matters because a killed runner is the common
/// case, not the rare one. A stale pid file would need a liveness check, and a pid on Windows is
/// recycled soon enough that the check needs a name check too.
/// </para>
/// </summary>
internal static class SuiteLock
{
	private static FileStream? _held;

	/// <summary>
	/// What a second suite exits with. Distinct from the runner's own codes so a caller can tell
	/// "another suite is running" from "tests failed", and nothing like the 5 an unrecognised option
	/// produces.
	/// </summary>
	private const int AlreadyRunning = 75;

	[Before(HookType.TestSession)]
	public static void Take()
	{
		var path = PathFor();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		try
		{
			// FileShare.Read so the suite that loses can still read who holds it. Nothing but this
			// takes the file for writing, so opening it is the claim.
			_held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
		}
		catch (IOException)
		{
			// Straight at the handle rather than through Console: the test platform redirects the
			// console for the session it is about to run, and a refusal nobody sees is no refusal.
			using (var stderr = Console.OpenStandardError())
			{
				var said = Encoding.UTF8.GetBytes(Refusal(path) + Environment.NewLine);
				stderr.Write(said, 0, said.Length);
				stderr.Flush();
			}

			Environment.Exit(AlreadyRunning);
			return;
		}

		var holder = new StringBuilder()
			.AppendLine($"pid {Environment.ProcessId}")
			.AppendLine($"started {DateTime.UtcNow:O}")
			.AppendLine($"from {Environment.CurrentDirectory}")
			.ToString();

		_held.Write(Encoding.UTF8.GetBytes(holder));
		_held.Flush();
	}

	[After(HookType.TestSession)]
	public static void Release()
	{
		_held?.Dispose();
		_held = null;
	}

	/// <summary>
	/// Beside the settings and the logs, in the vendor/product folder they already share, because
	/// that folder is per machine and this contention is per machine.
	/// </summary>
	private static string PathFor()
	{
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var root = localAppData.Length > 0 ? localAppData : Path.GetTempPath();

		return Path.Combine(root, "BinaryVibrance", "RoseMCP", "integration-suite.lock");
	}

	/// <summary>
	/// What the losing suite says. It names the holder, where it was started from and how to wait for
	/// it, because the reader is usually an agent in a different checkout that has no other way to
	/// know the machine is busy.
	/// </summary>
	private static string Refusal(string path)
	{
		var message = new StringBuilder();
		message.AppendLine("Another RoseMCP integration suite is already running on this machine, so this one has not started.");
		message.AppendLine();
		message.AppendLine(Holder(path));
		message.AppendLine();
		message.AppendLine("The suites share machine-wide state -- registered appx packages, deployed probe apps, attached");
		message.AppendLine("debuggers -- so running two at once corrupts both, whatever worktrees they were started from.");
		message.AppendLine();
		message.AppendLine("Wait for that process to exit, then start this suite again. A build of RoseMcp.LiveApp will also");
		message.AppendLine("fail while it runs, because the running suite holds the assemblies it copies.");
		message.AppendLine($"The lock is {path}; it is released when the holder exits, killed or not.");

		return message.ToString();
	}

	private static string Holder(string path)
	{
		try
		{
			using var reading = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using var reader = new StreamReader(reading);
			var written = reader.ReadToEnd().Trim();

			return written.Length > 0 ? written : "The holder did not say who it is.";
		}
		catch (Exception exception)
		{
			// Losing the race is the finding; failing to describe the winner is not worth a second
			// error on top of it.
			return $"The holder could not be read ({exception.GetType().Name}).";
		}
	}
}
