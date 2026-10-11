using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// Keeps every activity record this suite's brokers write in a folder of the run's own.
/// <para>
/// Most suites here register the broker as its hosts do and call its tools, and the broker records every call;
/// left to its default, each run would file a few hundred calls about fixtures nobody opened into the record a
/// person reads to see what their agents did. Set once before the first test, for every broker the run builds,
/// rather than in each test, since a test that forgets is exactly how the person's record fills up.
/// </para>
/// </summary>
public static class ActivityRecordIsolation
{
	private static string? _directory;

	[Before(HookType.TestSession)]
	public static void KeepTheRecordInTheRun()
	{
		_directory = Path.Combine(Path.GetTempPath(), "RoseMcp.UnitTests", "Activity", Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable(ActivityRecord.RootVariable, _directory);
	}

	[After(HookType.TestSession)]
	public static void ForgetTheRunsRecord()
	{
		if (_directory is null) return;

		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A broker a test left running still holds its file; the temp folder is the operating system's to clear.
		}
	}
}
