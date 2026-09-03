using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
	[Fact]
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
			TestContext.Current.CancellationToken,
			progress);

		load.Workspace.Dispose();

		var reports = progress.Reports;

		Assert.NotEmpty(reports);

		// The projects in the fixture, by name, because "loading" on its own does not tell anyone
		// which of forty projects is the slow one.
		Assert.Contains(reports, report => report.Message.Contains("Core", StringComparison.Ordinal));
		Assert.Contains(reports, report => report.Message.Contains("App", StringComparison.Ordinal));

		// A bar that goes backwards is worse than no bar, which is what the sliced scales exist to
		// prevent.
		AssertNeverGoesBackwards(reports);

		// Restore, the design-time build and the generator pass all have to have happened for a
		// load to be finished, so the last word cannot be an early phase.
		Assert.True(reports[^1].Percent >= 75, $"the load finished at {reports[^1].Percent}");
	}

	[Fact]
	public async Task A_diagnostics_pass_says_which_project_it_is_analysing()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var progress = new CapturingProgress();

		await new DiagnosticsService(NullLogger<DiagnosticsService>.Instance).AnalyseAsync(
			snapshot,
			new DiagnosticsRequest(),
			TestContext.Current.CancellationToken,
			progress);

		var reports = progress.Reports;

		Assert.Contains(reports, report => report.Message.StartsWith("Analysing", StringComparison.Ordinal));
		Assert.Contains(reports, report => report.Message.Contains("Core", StringComparison.Ordinal));
		AssertNeverGoesBackwards(reports);
	}

	/// <summary>
	/// The end-to-end check: a worker's progress notifications have to reach the broker's activity
	/// log, because that log is the only thing the tray window and the admin endpoint can read.
	/// </summary>
	[Fact]
	public async Task The_broker_records_the_load_a_worker_does_before_anyone_calls_it()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = new WorkspaceManager(
			Options.Create(new BrokerOptions { DefaultWorkspaceRoot = Path.GetTempPath() }),
			NullLoggerFactory.Instance,
			NullLogger<WorkspaceManager>.Instance);

		// No tool call of any kind: starting the worker is enough, which is what makes a reload from
		// the tray visible.
		await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current.CancellationToken);

		var load = await WaitForAsync(
			() => manager.Describe().SingleOrDefault()?.Recent
				.FirstOrDefault(activity => activity.Operation == WorkspaceWorker.LoadOperation),
			TimeSpan.FromMinutes(2));

		Assert.Equal(ActivityOutcome.Succeeded, load.Outcome);
		Assert.True(load.Elapsed > TimeSpan.Zero);

		// A message at all means a progress notification crossed the process boundary and was
		// matched to the right activity.
		//
		// Not the percentage, though it is tempting. The activity is completed by the call's
		// response, while progress arrives on a separate notification path, so which report is the
		// last one processed before the snapshot is a race between the two -- and a report carrying
		// no percentage deliberately clears it, since a sender that has stopped knowing must not
		// leave a bar frozen. On a fixture this small the load can finish with only such a report
		// seen. Percentages are covered where they can be observed in order, by the tests above.
		Assert.False(string.IsNullOrWhiteSpace(load.Message), "the load reported no progress");
	}

	private static void AssertNeverGoesBackwards(IReadOnlyList<(string Message, double? Percent)> reports)
	{
		var highest = 0d;

		foreach (var (message, percent) in reports)
		{
			if (percent is not { } value) continue;

			Assert.True(value >= highest, $"'{message}' reported {value} after {highest}");
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

			await Task.Delay(100, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException($"Nothing showed up within {timeout.TotalSeconds:F0}s.");
	}
}
