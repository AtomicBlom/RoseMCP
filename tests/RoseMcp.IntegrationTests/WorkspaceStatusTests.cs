using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
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
	[Test]
	public async Task Status_still_knows_what_the_load_cost_and_how_it_went()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var host = Host(fixture);

		await host.StartAsync(TestContext.Current!.Execution.CancellationToken);
		var status = await host.GetStatusAsync(TestContext.Current!.Execution.CancellationToken);

		status.LoadSeconds.ShouldBeGreaterThan(0, "a load that took no time did not happen");
		status.Restore.ShouldNotBeNull();
	}

	/// <summary>
	/// Null meant "did not multi-target" and was read as "has no framework", which is the signature
	/// of a solution loaded under a configuration it does not declare. A permanent false alarm on
	/// the one signal worth trusting.
	/// </summary>
	[Test]
	public async Task Every_project_reports_the_framework_it_was_built_for()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var host = Host(fixture);

		await host.StartAsync(TestContext.Current!.Execution.CancellationToken);
		var status = await host.GetStatusAsync(TestContext.Current!.Execution.CancellationToken);

		status.Projects.ShouldNotBeEmpty();
		foreach (var project in status.Projects)
		{
			string.IsNullOrWhiteSpace(project.TargetFramework).ShouldBeFalse(
						"every project reports the framework it was built for");
		}
	}

	/// <summary>
	/// MSBuild calls it a Failure when NuGet's vulnerability audit cannot reach its feed, naming
	/// projects that went on to compile perfectly. Blaming them for it marked most of a solution as
	/// failed, and a workspace that is always degraded says nothing.
	/// </summary>
	[Test]
	public async Task A_project_that_resolved_its_references_is_not_called_a_failure()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var host = Host(fixture);

		await host.StartAsync(TestContext.Current!.Execution.CancellationToken);
		var status = await host.GetStatusAsync(TestContext.Current!.Execution.CancellationToken);

		foreach (var project in status.Projects)
		{
			project.LoadedSuccessfully.ShouldBeTrue();
		}
		status.DegradedReasons.ShouldNotContain(reason => reason.Contains("did not load", StringComparison.Ordinal));
	}

	/// <summary>
	/// A project the design-time build loads and this worker's own MSBuild cannot evaluate is the shape of a
	/// worker that has lost its SDK: the build host is a process of its own and still loads everything, so
	/// nothing else in the status says anything is wrong. The load's report has to say so, and so does every
	/// status after it, which re-describes the snapshot and would drop a fact that belongs to the load.
	/// <para>
	/// The project chooses an SDK that does not exist only where neither the design-time build's nor
	/// restore's property is set, which is exactly the worker's own evaluation.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_project_the_worker_cannot_evaluate_degrades_the_load_and_every_status_after_it()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		await File.WriteAllTextAsync(fixture.Path("Core", "Core.csproj"), """
			<Project>
			  <PropertyGroup>
			    <InWorkerEvaluation>true</InWorkerEvaluation>
			    <InWorkerEvaluation Condition="'$(DesignTimeBuild)' == 'true' or '$(MSBuildIsRestoring)' == 'true' or '$(ExcludeRestorePackageImports)' == 'true'">false</InWorkerEvaluation>
			  </PropertyGroup>
			  <Import Project="Sdk.props" Sdk="RoseMcp.Fixture.Missing.Sdk" Condition="'$(InWorkerEvaluation)' == 'true'" />
			  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" Condition="'$(InWorkerEvaluation)' != 'true'" />
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <Nullable>enable</Nullable>
			    <ImplicitUsings>enable</ImplicitUsings>
			  </PropertyGroup>
			  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" Condition="'$(InWorkerEvaluation)' != 'true'" />
			</Project>
			""", token);

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var load = await loader.LoadAsync(new WorkerOptions { SolutionPath = fixture.SolutionPath }, token);
		load.Workspace.Dispose();

		load.Report.Projects.ShouldAllBe(project => project.LoadedSuccessfully, "the design-time build loads Core");
		load.Report.State.ShouldBe(WorkspaceState.Degraded);
		load.Report.EvaluationFailures.Select(failure => Path.GetFileName(failure.Project)).ShouldBe(["Core.csproj"]);
		load.Report.EvaluationFailures[0].NamesSdk.ShouldBeTrue();
		load.Report.DegradedReasons.ShouldContain(
			reason => reason.StartsWith("1 project that names an SDK could not be evaluated", StringComparison.Ordinal));

		await using var host = Host(fixture);
		await host.StartAsync(token);

		var first = await host.GetStatusAsync(token);
		var later = await host.GetStatusAsync(token);

		foreach (var status in new[] { first, later })
		{
			status.State.ShouldBe(WorkspaceState.Degraded);
			status.EvaluationFailures.Count.ShouldBe(1);
			status.DegradedReasons.ShouldContain(reason => reason.Contains("Core", StringComparison.Ordinal)
				&& reason.Contains("could not be evaluated", StringComparison.Ordinal));
		}
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
