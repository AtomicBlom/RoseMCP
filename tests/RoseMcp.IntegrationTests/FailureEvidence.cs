using System.Globalization;

using RoseMcp.Logging;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a failing test is handed: every log a Rose process wrote while it ran.
/// <para>
/// The evidence for an integration failure is almost never in its assertion. It is in the logs of the
/// processes the test started -- a worker that never answered initialize, a host whose hold and
/// auto-continue crossed, a tree read two elements short -- and each of those processes logs to the
/// machine's shared folder, pruned to the newest twenty sessions per component. A full suite starts
/// far more hosts than that, so the host log of a failure early in a run is deleted by the suite
/// itself before anybody reads it, and on a hosted runner the folder goes with the runner.
/// </para>
/// <para>
/// So every process this suite starts logs into one directory per run, which nothing prunes, and a
/// test that fails is handed every log written while it ran, attached to its result. When tests
/// overlap that is more than its own logs, and deliberately so: under load, the process that took the
/// machine's attention is as likely to be the cause as the one the test started -- and a failure that
/// appears only under load is one a developer running several apps at once will meet.
/// </para>
/// </summary>
internal static class FailureEvidence
{
	/// <summary>Runs whose logs are kept, so a developer's machine does not fill with old ones.</summary>
	private const int RunsKept = 5;

	/// <summary>
	/// How far either side of a test's own start and end a log still counts as written while it ran. A
	/// host's log is claimed a moment before the call that starts it returns and is written to a moment
	/// after the test's last call, and a file's time is only as fine as the filesystem keeps it.
	/// </summary>
	private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

	/// <summary>This run's directory. Under the test results, so the upload that carries the report carries these.</summary>
	internal static string? RunDirectory { get; private set; }

	/// <summary>
	/// Points every Rose process this run starts at one directory. Set in this process before the first
	/// test, so each host, worker and server the suite starts inherits it through its environment.
	/// </summary>
	[Before(HookType.TestSession)]
	public static void OpenRunDirectory()
	{
		var root = Path.Combine(AppContext.BaseDirectory, "TestResults", "logs");
		ForgetOldRuns(root);

		var run = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
		Directory.CreateDirectory(run);

		Environment.SetEnvironmentVariable(RoseLogFile.RootVariable, run);
		RunDirectory = run;
	}

	/// <summary>
	/// Attaches to a failed, timed-out or cancelled test every log written while it ran, and says in its
	/// output which they were, so the failure carries what each process was doing rather than only the
	/// line that noticed.
	/// </summary>
	[AfterEvery(HookType.Test)]
	public static void AttachToFailure(TestContext context)
	{
		var result = context.Execution.Result;
		if (RunDirectory is null || result is null) return;
		if (result.State is not (TestState.Failed or TestState.Timeout or TestState.Cancelled)) return;

		var from = (result.Start ?? DateTimeOffset.UtcNow) - Slack;
		var to = (result.End ?? DateTimeOffset.UtcNow) + Slack;

		var written = WrittenBetween(from, to);
		if (written.Count == 0)
		{
			context.Output.WriteLine($"No Rose process wrote a log under {RunDirectory} while this test ran.");
			return;
		}

		context.Output.WriteLine(
			$"{written.Count} log(s) were written while this test ran, attached and kept under {RunDirectory}:");

		foreach (var file in written)
		{
			var name = Path.GetRelativePath(RunDirectory, file);
			context.Output.WriteLine($"  {name}");
			context.Output.AttachArtifact(file, name, "Written while the test ran");
		}
	}

	/// <summary>
	/// The logs in this run's directory, and the tap logs of every provider sandbox, written between two
	/// instants. A tap log is copied into the run's directory first: it lives in its host's sandbox folder,
	/// which the next host to start deletes once its own host is gone.
	/// </summary>
	private static List<string> WrittenBetween(DateTimeOffset from, DateTimeOffset to)
	{
		bool During(FileInfo file) =>
			file.LastWriteTimeUtc >= from.UtcDateTime && file.CreationTimeUtc <= to.UtcDateTime;

		var written = new List<string>();

		try
		{
			written.AddRange(new DirectoryInfo(RunDirectory!).EnumerateFiles("*.log", SearchOption.AllDirectories)
				.Where(During)
				.OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
				.Select(file => file.FullName));

			var sandboxes = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "RoseMcpXaml"));
			if (sandboxes.Exists)
			{
				var copies = Path.Combine(RunDirectory!, "Tap");
				foreach (var tap in sandboxes.EnumerateFiles("*.log", SearchOption.AllDirectories).Where(During))
				{
					Directory.CreateDirectory(copies);

					// Named for the host whose sandbox it was in, which is how the host's own log names it.
					var copy = Path.Combine(copies, $"{tap.Directory!.Name}-{tap.Name}");
					tap.CopyTo(copy, overwrite: true);
					written.Add(copy);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Evidence is gathered best effort. A log another process holds, or a sandbox deleted as it was
			// read, costs that one file, and failing the hook would put a second failure on the test.
		}

		return written;
	}

	/// <summary>Deletes all but the newest runs' directories, newest judged by name, which is a UTC timestamp.</summary>
	private static void ForgetOldRuns(string root)
	{
		if (!Directory.Exists(root)) return;

		foreach (var old in Directory.EnumerateDirectories(root).OrderDescending(StringComparer.Ordinal).Skip(RunsKept - 1))
		{
			try
			{
				Directory.Delete(old, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// A run whose logs something still holds open is kept until a later run can take it.
			}
		}
	}
}
