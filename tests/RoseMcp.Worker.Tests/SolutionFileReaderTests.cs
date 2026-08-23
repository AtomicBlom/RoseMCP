namespace RoseMcp.Worker.Tests;

public sealed class SolutionFileReaderTests
{
	[Fact]
	public void Reads_projects_from_a_classic_sln()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var projects = SolutionFileReader.ReadProjectPaths(fixture.SolutionPath);

		Assert.Equal(["App.csproj", "Core.csproj"], projects.Select(Path.GetFileName).Order());
		Assert.All(projects, path => Assert.True(Path.IsPathFullyQualified(path)));
		Assert.All(projects, path => Assert.True(File.Exists(path)));
	}

	[Fact]
	public void Reads_projects_from_an_slnx()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		var projects = SolutionFileReader.ReadProjectPaths(fixture.SolutionPath);

		Assert.Equal(["Consumer.csproj", "Gen.csproj"], projects.Select(Path.GetFileName).Order());
		Assert.All(projects, path => Assert.True(File.Exists(path)));
	}

	[Fact]
	public void Treats_a_bare_project_path_as_a_single_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var project = fixture.Path("Simple", "Core", "Core.csproj");

		var projects = SolutionFileReader.ReadProjectPaths(project);

		Assert.Equal([project], projects);
	}
}
