using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.IntegrationTests;

public sealed class SolutionLoaderTests
{
	[Test]
	public async Task Loads_every_project_in_a_classic_sln()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		using var load = await LoadAsync(fixture);
		var report = load.Result.Report;

		report.State.ShouldBe(WorkspaceState.Loaded);
		report.DegradedReasons.ShouldBeEmpty();
		report.Projects.Select(project => project.Name).Order().ShouldBe(["App", "Core"]);
		foreach (var project in report.Projects)
		{
			project.LoadedSuccessfully.ShouldBeTrue();
		}
	}

	[Test]
	public async Task Restores_when_there_is_no_restore_output_and_skips_when_there_is()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		using (var first = await LoadAsync(fixture))
		{
			(first.Result.Report.Restore?.Ran).ShouldBe(true);
			(first.Result.Report.Restore?.Succeeded).ShouldBe(true);
		}

		using var second = await LoadAsync(fixture);
		(second.Result.Report.Restore?.Ran).ShouldBe(false, "the second load skips a restore it does not need");

		// A skipped restore must not read as a failed one.
		(second.Result.Report.Restore?.Succeeded).ShouldBeNull();
	}

	[Test]
	public async Task Runs_source_generators_once_the_generator_project_is_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		using var load = await LoadAsync(fixture);
		var report = load.Result.Report;

		var consumer = report.Projects.Single(project => project.Name == "Consumer");

		report.State.ShouldBe(WorkspaceState.Loaded);
		report.DegradedReasons.ShouldBeEmpty();
		consumer.MissingAnalyzerOutputs.ShouldBeEmpty();

		// GreetableAttribute.g.cs from post-initialisation, plus Widget.Greeting.g.cs.
		consumer.GeneratedDocumentCount.ShouldBe(2);
	}

	/// <summary>
	/// The regression this project exists for. With the generator project unbuilt, MSBuild still
	/// passes its expected output to the compiler, so the workspace loads without complaint and
	/// simply produces no generated code. Reporting that as a healthy load is the bug.
	/// </summary>
	[Test]
	public async Task Reports_degraded_when_an_in_solution_generator_has_not_been_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		using var load = await LoadAsync(fixture);
		var report = load.Result.Report;

		var consumer = report.Projects.Single(project => project.Name == "Consumer");

		report.State.ShouldBe(WorkspaceState.Degraded);
		consumer.GeneratedDocumentCount.ShouldBe(0);
		consumer.MissingAnalyzerOutputs.ShouldContain("Gen");

		// The reason has to be actionable, not just true.
		var reason = report.DegradedReasons.ShouldHaveSingleItem();
		reason.ShouldContain("Gen", Case.Sensitive);
		reason.ShouldContain("dotnet build", Case.Sensitive);
		reason.ShouldContain("Gen.csproj", Case.Sensitive);
	}

	[Test]
	public async Task Recovers_from_degraded_once_the_generator_is_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		using (var degraded = await LoadAsync(fixture))
		{
			degraded.Result.Report.State.ShouldBe(WorkspaceState.Degraded);
		}

		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		using var healthy = await LoadAsync(fixture);
		var consumer = healthy.Result.Report.Projects.Single(project => project.Name == "Consumer");

		healthy.Result.Report.State.ShouldBe(WorkspaceState.Loaded);
		consumer.GeneratedDocumentCount.ShouldBe(2);
	}

	/// <summary>
	/// The whole point of the config file is that no call has to be made first, so the load has to
	/// find it by itself. Release rather than a Revit-shaped name because the fixture has to build:
	/// what is under test is that the file was read and obeyed, not what it said.
	/// </summary>
	[Test]
	public async Task Loads_under_the_properties_a_config_file_pins()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		await File.WriteAllTextAsync(
			Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, WorkspaceConfigFile.FileName),
			"{ \"configuration\": \"Release\" }",
			TestContext.Current!.Execution.CancellationToken);

		using var load = await LoadAsync(fixture);

		load.Result.Build.Configuration.ShouldBe("Release");
		load.Result.Report.BuildConfiguration.ShouldBe("Release|AnyCPU");
		// Among the notices rather than the only one: a fixture nobody has built also gets told that
		// its output is older than its sources, which is true and beside the point here.
		load.Result.Report.Notices.ShouldContain(
			notice => notice.Contains(WorkspaceConfigFile.FileName, StringComparison.Ordinal));
	}

	private static async Task<LoadScope> LoadAsync(FixtureSolution fixture)
	{
		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var options = new WorkerOptions { SolutionPath = fixture.SolutionPath };
		return new LoadScope(await loader.LoadAsync(options, TestContext.Current!.Execution.CancellationToken));
	}

	/// <summary>Disposes the workspace a load produced, which the caller owns.</summary>
	private sealed class LoadScope(LoadResult result) : IDisposable
	{
		public LoadResult Result { get; } = result;

		public void Dispose() => Result.Workspace.Dispose();
	}
}
