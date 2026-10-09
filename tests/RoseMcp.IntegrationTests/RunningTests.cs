using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Names the tests still running when the run is cut short.
/// <para>
/// The runner prints a test when it finishes, so a run cut short -- by its own <c>--timeout</c>, by
/// Ctrl+C, or by CI stopping a step that ran out of time -- says what finished and nothing about what
/// had not, and a test it cancels is not reported at all. The test it stopped in is the one a reader
/// needs, whether the run hung there or simply ran out of time. So every test is noted as it starts and
/// forgotten as it ends, and whatever is left when the run is cancelled, interrupted or exits is written
/// to stderr.
/// </para>
/// </summary>
internal static class RunningTests
{
	private static readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Started)> Running = new();

	private static int _named;

	/// <summary>
	/// Listens for the three ways a run ends early: the session's own cancellation, which the runner's
	/// <c>--timeout</c> raises; Ctrl+C; and the process exiting, which also covers a normal end, when
	/// nothing is left to name.
	/// </summary>
	[Before(HookType.TestSession)]
	public static void Watch(TestSessionContext session)
	{
		session.SessionCancellationToken.Register(() => NameWhatIsRunning("cancelled"));
		Console.CancelKeyPress += (_, _) => NameWhatIsRunning("interrupted");
		AppDomain.CurrentDomain.ProcessExit += (_, _) => NameWhatIsRunning("ending");
	}

	/// <summary>Notes a test as running, by class and display name, with the time it started.</summary>
	[BeforeEvery(HookType.Test)]
	public static void Started(TestContext context)
	{
		var name = $"{context.Metadata.TestDetails.ClassType.Name}.{context.Metadata.DisplayName}";
		Running[context.Id] = (name, DateTimeOffset.UtcNow);
	}

	/// <summary>
	/// Forgets a test that has finished, unless it ended cancelled. A cancelled run unwinds the tests it
	/// was running before the process exits, so forgetting those here would leave the exit with nothing
	/// to name -- and they are exactly the tests the run stopped in.
	/// </summary>
	[AfterEvery(HookType.Test)]
	public static void Finished(TestContext context)
	{
		var cancelled = context.Execution.Result?.State == TestState.Cancelled;
		if (!cancelled) Running.TryRemove(context.Id, out _);
	}

	/// <summary>Writes the list once, from whichever of the three signals comes first with something on it.</summary>
	private static void NameWhatIsRunning(string how)
	{
		var now = DateTimeOffset.UtcNow;
		var still = Running.Values.OrderBy(test => test.Started).ToList();
		if (still.Count == 0 || Interlocked.Exchange(ref _named, 1) != 0) return;

		var message = new StringBuilder();
		message.AppendLine();
		message.AppendLine(CultureInfo.InvariantCulture, $"The run is {how} with {still.Count} test(s) unfinished:");

		foreach (var (name, started) in still)
		{
			message.AppendLine(CultureInfo.InvariantCulture, $"  [{now - started:hh\\:mm\\:ss}] {name}");
		}

		// Straight at the handle rather than through Console: the test platform redirects the console
		// into whichever test is current, and a cancelled test's output is never shown.
		using var stderr = Console.OpenStandardError();
		var said = Encoding.UTF8.GetBytes(message.ToString());
		stderr.Write(said, 0, said.Length);
		stderr.Flush();
	}
}
