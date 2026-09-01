using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tests;

public sealed class SolutionLoaderTests
{
	[Fact]
	public async Task Loads_every_project_in_a_classic_sln()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		using var load = await LoadAsync(fixture);
		var report = load.Result.Report;

		Assert.Equal(WorkspaceState.Loaded, report.State);
		Assert.Empty(report.DegradedReasons);
		Assert.Equal(["App", "Core"], report.Projects.Select(project => project.Name).Order());
		Assert.All(report.Projects, project => Assert.True(project.LoadedSuccessfully));
	}

	[Fact]
	public async Task Restores_when_there_is_no_restore_output_and_skips_when_there_is()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		using (var first = await LoadAsync(fixture))
		{
			Assert.True(first.Result.Report.Restore?.Ran);
			Assert.True(first.Result.Report.Restore?.Succeeded);
		}

		using var second = await LoadAsync(fixture);
		Assert.False(second.Result.Report.Restore?.Ran);

		// A skipped restore must not read as a failed one.
		Assert.Null(second.Result.Report.Restore?.Succeeded);
	}

	[Fact]
	public async Task Runs_source_generators_once_the_generator_project_is_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		using var load = await LoadAsync(fixture);
		var report = load.Result.Report;

		var consumer = report.Projects.Single(project => project.Name == "Consumer");

		Assert.Equal(WorkspaceState.Loaded, report.State);
		Assert.Empty(report.DegradedReasons);
		Assert.Empty(consumer.MissingAnalyzerOutputs);

		// GreetableAttribute.g.cs from post-initialisation, plus Widget.Greeting.g.cs.
		Assert.Equal(2, consumer.GeneratedDocumentCount);
	}

	/// <summary>
	/// The regression this project exists for. With the generator project unbuilt, MSBuild still
	/// passes its expected output to the compiler, so the workspace loads without complaint and
	/// simply produces no generated code. Reporting that as a healthy load is the bug.
	/// </summary>
	[Fact]
	public async Task Reports_degraded_when_an_in_solution_generator_has_not_been_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		using var load = await LoadAsync(fixture);
		var report = load.Result.Report;

		var consumer = report.Projects.Single(project => project.Name == "Consumer");

		Assert.Equal(WorkspaceState.Degraded, report.State);
		Assert.Equal(0, consumer.GeneratedDocumentCount);
		Assert.Contains("Gen", consumer.MissingAnalyzerOutputs);

		// The reason has to be actionable, not just true.
		var reason = Assert.Single(report.DegradedReasons);
		Assert.Contains("Gen", reason, StringComparison.Ordinal);
		Assert.Contains("dotnet build", reason, StringComparison.Ordinal);
		Assert.Contains("Gen.csproj", reason, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Recovers_from_degraded_once_the_generator_is_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		using (var degraded = await LoadAsync(fixture))
		{
			Assert.Equal(WorkspaceState.Degraded, degraded.Result.Report.State);
		}

		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		using var healthy = await LoadAsync(fixture);
		var consumer = healthy.Result.Report.Projects.Single(project => project.Name == "Consumer");

		Assert.Equal(WorkspaceState.Loaded, healthy.Result.Report.State);
		Assert.Equal(2, consumer.GeneratedDocumentCount);
	}

	/// <summary>
	/// The whole point of the config file is that no call has to be made first, so the load has to
	/// find it by itself. Release rather than a Revit-shaped name because the fixture has to build:
	/// what is under test is that the file was read and obeyed, not what it said.
	/// </summary>
	[Fact]
	public async Task Loads_under_the_properties_a_config_file_pins()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		await File.WriteAllTextAsync(
			Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, WorkspaceConfigFile.FileName),
			"{ \"configuration\": \"Release\" }",
			TestContext.Current.CancellationToken);

		using var load = await LoadAsync(fixture);

		Assert.Equal("Release", load.Result.Build.Configuration);
		Assert.Equal("Release|AnyCPU", load.Result.Report.BuildConfiguration);
		Assert.Contains(WorkspaceConfigFile.FileName, load.Result.Report.Notices.Single());
	}

	private static async Task<LoadScope> LoadAsync(FixtureSolution fixture)
	{
		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var options = new WorkerOptions { SolutionPath = fixture.SolutionPath };
		return new LoadScope(await loader.LoadAsync(options, TestContext.Current.CancellationToken));
	}

	/// <summary>Disposes the workspace a load produced, which the caller owns.</summary>
	private sealed class LoadScope(LoadResult result) : IDisposable
	{
		public LoadResult Result { get; } = result;

		public void Dispose() => Result.Workspace.Dispose();
	}
}
