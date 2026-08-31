namespace RoseMcp.Worker.Tests;

public sealed class SolutionFileReaderTests : IDisposable
{
	private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("rosemcp-solutions-");

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
	public void Reads_the_configurations_an_slnx_declares()
	{
		// The BuildType and Platform elements under a Project map a solution configuration onto a
		// project one. Counting those would offer the caller configurations the solution does not have.
		var path = Write("Revit.slnx", """
			<Solution>
			  <Configurations>
			    <BuildType Name="Debug-2024" />
			    <BuildType Name="Release" />
			    <Platform Name="x64" />
			  </Configurations>
			  <Project Path="A/A.csproj">
			    <BuildType Solution="Debug-2024|*" Project="Debug" />
			    <Platform Project="x64" />
			  </Project>
			</Solution>
			""");

		var configurations = SolutionFileReader.ReadConfigurations(path);

		Assert.Equal(["Debug-2024", "Release"], configurations.Configurations);
		Assert.Equal(["x64"], configurations.Platforms);
	}

	[Fact]
	public void Reads_the_configurations_a_classic_sln_declares()
	{
		// Only the solution-wide section: the per-project one repeats the same names with a GUID
		// attached, and one of them here is a configuration the solution itself does not offer.
		var path = Write("Legacy.sln", """
			Microsoft Visual Studio Solution File, Format Version 12.00
			Global
				GlobalSection(SolutionConfigurationPlatforms) = preSolution
					Debug|x64 = Debug|x64
					Debug|Any CPU = Debug|Any CPU
					Release|x64 = Release|x64
				EndGlobalSection
				GlobalSection(ProjectConfigurationPlatforms) = postSolution
					{0000}.Retail|x64.ActiveCfg = Retail|x64
				EndGlobalSection
			EndGlobal
			""");

		var configurations = SolutionFileReader.ReadConfigurations(path);

		Assert.Equal(["Debug", "Release"], configurations.Configurations);

		// Spelled as MSBuild spells the property, not as the solution file spells it. Passing a
		// project "Any CPU" with the space moves its output to bin\Any CPU\.
		Assert.Equal(["x64", "AnyCPU"], configurations.Platforms);
	}

	[Fact]
	public void Reads_the_configurations_a_bare_project_declares()
	{
		var path = Write("A.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <Configurations>Release;Debug-2024;Debug-2025</Configurations>
			    <Platforms>x64</Platforms>
			  </PropertyGroup>
			</Project>
			""");

		var configurations = SolutionFileReader.ReadConfigurations(path);

		Assert.Equal(["Release", "Debug-2024", "Debug-2025"], configurations.Configurations);
		Assert.Equal(["x64"], configurations.Platforms);
	}

	[Fact]
	public void Declares_nothing_for_a_solution_that_declares_nothing()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		Assert.True(SolutionFileReader.ReadConfigurations(fixture.SolutionPath).IsEmpty);
	}

	[Fact]
	public void Treats_a_bare_project_path_as_a_single_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var project = fixture.Path("Simple", "Core", "Core.csproj");

		var projects = SolutionFileReader.ReadProjectPaths(project);

		Assert.Equal([project], projects);
	}

	public void Dispose() => _temporary.Delete(recursive: true);

	private string Write(string name, string content)
	{
		var path = Path.Combine(_temporary.FullName, name);
		File.WriteAllText(path, content);

		return path;
	}
}
