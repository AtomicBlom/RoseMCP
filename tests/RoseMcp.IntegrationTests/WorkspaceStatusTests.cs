using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Status is what a caller consults to decide whether to trust everything else, so a field it
/// cannot fill is worse than a field it does not have.
/// </summary>
public sealed class WorkspaceStatusTests
{
	/// <summary>
	/// These three belong to the load, not to the snapshot status re-describes, and were dropped on
	/// the way through -- every status answer reported a load time of zero, no restore and no load
	/// diagnostics, on every solution.
	/// <para>
	/// The restore one was not merely missing. A failed restore reaches degradedReasons only through
	/// that field, so with nothing to read the workspace called itself healthy in exactly the
	/// situation it exists to warn about.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Status_still_knows_what_the_load_cost_and_how_it_went()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var host = Host(fixture);

		await host.StartAsync(TestContext.Current.CancellationToken);
		var status = await host.GetStatusAsync(TestContext.Current.CancellationToken);

		Assert.True(status.LoadSeconds > 0, "a load that took no time did not happen");
		Assert.NotNull(status.Restore);
	}

	/// <summary>
	/// Null meant "did not multi-target" and was read as "has no framework", which is the signature
	/// of a solution loaded under a configuration it does not declare. A permanent false alarm on
	/// the one signal worth trusting.
	/// </summary>
	[Fact]
	public async Task Every_project_reports_the_framework_it_was_built_for()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var host = Host(fixture);

		await host.StartAsync(TestContext.Current.CancellationToken);
		var status = await host.GetStatusAsync(TestContext.Current.CancellationToken);

		Assert.NotEmpty(status.Projects);
		Assert.All(status.Projects, project => Assert.False(string.IsNullOrWhiteSpace(project.TargetFramework)));
	}

	/// <summary>
	/// MSBuild calls it a Failure when NuGet's vulnerability audit cannot reach its feed, naming
	/// projects that went on to compile perfectly. Blaming them for it marked most of a solution as
	/// failed, and a workspace that is always degraded says nothing.
	/// </summary>
	[Fact]
	public async Task A_project_that_resolved_its_references_is_not_called_a_failure()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var host = Host(fixture);

		await host.StartAsync(TestContext.Current.CancellationToken);
		var status = await host.GetStatusAsync(TestContext.Current.CancellationToken);

		Assert.All(status.Projects, project => Assert.True(project.LoadedSuccessfully));
		Assert.DoesNotContain(status.DegradedReasons, reason => reason.Contains("did not load", StringComparison.Ordinal));
	}

	private static WorkspaceHost Host(FixtureSolution fixture) => new(
		new WorkerOptions { SolutionPath = fixture.SolutionPath },
		new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance),
		new SharedWorkProgress(),
		NullLoggerFactory.Instance,
		new NeverStops(),
		NullLogger<WorkspaceHost>.Instance);
}
