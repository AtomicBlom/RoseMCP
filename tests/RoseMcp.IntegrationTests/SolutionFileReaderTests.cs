using RoseMcp.Solutions;

namespace RoseMcp.IntegrationTests;

public sealed class SolutionFileReaderTests : IDisposable
{
	private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("rosemcp-solutions-");

	[Test]
	public void Reads_projects_from_a_classic_sln()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var projects = SolutionFileReader.ReadProjectPaths(fixture.SolutionPath);

		projects.Select(Path.GetFileName).Order().ShouldBe(["App.csproj", "Core.csproj"]);
		foreach (var path in projects)
		{
			Path.IsPathFullyQualified(path).ShouldBeTrue();
		}
		foreach (var path in projects)
		{
			File.Exists(path).ShouldBeTrue();
		}
	}

	[Test]
	public void Reads_projects_from_an_slnx()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		var projects = SolutionFileReader.ReadProjectPaths(fixture.SolutionPath);

		projects.Select(Path.GetFileName).Order().ShouldBe(["Consumer.csproj", "Gen.csproj"]);
		foreach (var path in projects)
		{
			File.Exists(path).ShouldBeTrue();
		}
	}

	[Test]
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

		configurations.Configurations.ShouldBe(["Debug-2024", "Release"]);
		configurations.Platforms.ShouldBe(["x64"]);
	}

	[Test]
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

		configurations.Configurations.ShouldBe(["Debug", "Release"]);

		// Spelled as MSBuild spells the property, not as the solution file spells it. Passing a
		// project "Any CPU" with the space moves its output to bin\Any CPU\.
		configurations.Platforms.ShouldBe(["x64", "AnyCPU"]);
	}

	[Test]
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

		configurations.Configurations.ShouldBe(["Release", "Debug-2024", "Debug-2025"]);
		configurations.Platforms.ShouldBe(["x64"]);
	}

	[Test]
	public void Declares_nothing_for_a_solution_that_declares_nothing()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		SolutionFileReader.ReadConfigurations(fixture.SolutionPath).IsEmpty.ShouldBeTrue();
	}

	[Test]
	public void Treats_a_bare_project_path_as_a_single_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var project = fixture.Path("Simple", "Core", "Core.csproj");

		var projects = SolutionFileReader.ReadProjectPaths(project);

		projects.ShouldBe([project]);
	}

	public void Dispose() => _temporary.Delete(recursive: true);

	private string Write(string name, string content)
	{
		var path = Path.Combine(_temporary.FullName, name);
		File.WriteAllText(path, content);

		return path;
	}
}
