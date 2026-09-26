using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A warm Roslyn host looks identical from the outside whether it is idle or minutes into a
/// design-time build, so these check that the slow paths say where they have got to -- and that
/// what they say survives the trip across the process boundary to the broker.
/// </summary>
public sealed class ProgressReportingTests
{
	[Test]
	public async Task A_solution_load_says_which_project_it_is_on()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var progress = new CapturingProgress();

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var load = await loader.LoadAsync(
			new WorkerOptions { SolutionPath = fixture.SolutionPath },
			TestContext.Current!.Execution.CancellationToken,
			progress);

		load.Workspace.Dispose();

		var reports = progress.Reports;

		reports.ShouldNotBeEmpty();

		// The projects in the fixture, by name, because "loading" on its own does not tell anyone
		// which of forty projects is the slow one.
		reports.ShouldContain(report => report.Message.Contains("Core", StringComparison.Ordinal));
		reports.ShouldContain(report => report.Message.Contains("App", StringComparison.Ordinal));

		// A bar that goes backwards is worse than no bar, which is what the sliced scales exist to
		// prevent.
		AssertNeverGoesBackwards(reports);

		// Restore, the design-time build and the generator pass all have to have happened for a
		// load to be finished, so the last word cannot be an early phase.
		(reports[^1].Percent >= 75).ShouldBeTrue($"the load finished at {reports[^1].Percent}");
	}

	[Test]
	public async Task A_diagnostics_pass_says_which_project_it_is_analysing()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var progress = new CapturingProgress();

		await new DiagnosticsService(NullLogger<DiagnosticsService>.Instance).AnalyseAsync(
			snapshot,
			new DiagnosticsRequest(),
			TestContext.Current!.Execution.CancellationToken,
			progress);

		var reports = progress.Reports;

		reports.ShouldContain(report => report.Message.StartsWith("Analysing", StringComparison.Ordinal));
		reports.ShouldContain(report => report.Message.Contains("Core", StringComparison.Ordinal));
		AssertNeverGoesBackwards(reports);
	}

	/// <summary>
	/// The end-to-end check: a worker's progress notifications have to reach the broker's activity
	/// log, because that log is the only thing the tray window and the admin endpoint can read.
	/// </summary>
	[Test]
	public async Task The_broker_records_the_load_a_worker_does_before_anyone_calls_it()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = BrokerHarness.CreateManager(Path.GetTempPath());

		// No tool call of any kind: starting the worker is enough, which is what makes a reload from
		// the tray visible.
		await manager.GetOrStartAsync(WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), TestContext.Current!.Execution.CancellationToken);

		var load = await WaitForAsync(
			() => manager.Describe().SingleOrDefault()?.Recent
				.FirstOrDefault(activity => activity.Operation == WorkspaceWorker.LoadOperation),
			TimeSpan.FromMinutes(2));

		load.Outcome.ShouldBe(ActivityOutcome.Succeeded);
		(load.Elapsed > TimeSpan.Zero).ShouldBeTrue();

		// A message at all means a progress notification crossed the process boundary and was
		// matched to the right activity.
		//
		// Not the percentage, though it is tempting. The activity is completed by the call's
		// response, while progress arrives on a separate notification path, so which report is the
		// last one processed before the snapshot is a race between the two -- and a report carrying
		// no percentage deliberately clears it, since a sender that has stopped knowing must not
		// leave a bar frozen. On a fixture this small the load can finish with only such a report
		// seen. Percentages are covered where they can be observed in order, by the tests above.
		string.IsNullOrWhiteSpace(load.Message).ShouldBeFalse("the load reported no progress");
	}

	private static void AssertNeverGoesBackwards(IReadOnlyList<(string Message, double? Percent)> reports)
	{
		var highest = 0d;

		foreach (var (message, percent) in reports)
		{
			if (percent is not { } value) continue;

			(value >= highest).ShouldBeTrue($"'{message}' reported {value} after {highest}");
			highest = value;
		}
	}

	/// <summary>Waits for something to show up, since progress is reported by another thread.</summary>
	private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout)
		where T : class
	{
		var deadline = DateTime.UtcNow + timeout;

		while (DateTime.UtcNow < deadline)
		{
			if (probe() is { } found) return found;

			await Task.Delay(100, TestContext.Current!.Execution.CancellationToken);
		}

		throw new TimeoutException($"Nothing showed up within {timeout.TotalSeconds:F0}s.");
	}
}
