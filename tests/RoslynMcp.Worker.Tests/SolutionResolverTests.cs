using RoslynMcp.Broker;

namespace RoslynMcp.Worker.Tests;

/// <summary>
/// Agents refer to code by whatever file they are looking at, not by solution path, so resolution
/// has to work from anywhere inside the tree. Getting this wrong pushes the search onto the caller.
/// </summary>
public sealed class SolutionResolverTests
{
	[Fact]
	public void Resolves_a_source_file_to_its_enclosing_solution()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var resolved = SolutionResolver.Resolve(fixture.Path("Simple", "Core", "Calculator.cs"));

		Assert.Equal(fixture.SolutionPath, resolved, ignoreCase: true);
	}

	[Fact]
	public void Resolves_a_directory_to_its_enclosing_solution()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var resolved = SolutionResolver.Resolve(fixture.Path("Simple", "App"));

		Assert.Equal(fixture.SolutionPath, resolved, ignoreCase: true);
	}

	[Fact]
	public void Passes_a_solution_path_straight_through()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		Assert.Equal(fixture.SolutionPath, SolutionResolver.Resolve(fixture.SolutionPath), ignoreCase: true);
	}

	/// <summary>A project with no solution above it is still perfectly loadable.</summary>
	[Fact]
	public void Falls_back_to_a_bare_project_when_no_solution_encloses_it()
	{
		var root = Path.Combine(Path.GetTempPath(), "roslynmcp-tests", $"bare-{Guid.NewGuid():N}");
		var projectDirectory = Path.Combine(root, "Lonely");
		Directory.CreateDirectory(projectDirectory);

		var project = Path.Combine(projectDirectory, "Lonely.csproj");
		File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		File.WriteAllText(Path.Combine(projectDirectory, "Thing.cs"), "public sealed class Thing;");

		try
		{
			Assert.Equal(project, SolutionResolver.Resolve(project), ignoreCase: true);
			Assert.Equal(project, SolutionResolver.Resolve(Path.Combine(projectDirectory, "Thing.cs")), ignoreCase: true);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Says_what_to_pass_when_nothing_can_be_resolved()
	{
		var empty = Path.Combine(Path.GetTempPath(), "roslynmcp-tests", $"empty-{Guid.NewGuid():N}");
		Directory.CreateDirectory(empty);

		try
		{
			var error = Assert.Throws<ArgumentException>(() => SolutionResolver.Resolve(empty));
			Assert.Contains(".sln", error.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(empty, recursive: true);
		}
	}
}
