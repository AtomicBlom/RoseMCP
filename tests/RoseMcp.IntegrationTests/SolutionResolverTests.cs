using RoseMcp.Broker;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

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
		var root = Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"bare-{Guid.NewGuid():N}");
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
		var error = Assert.Throws<ArgumentException>(() => SolutionResolver.Resolve(NowhereDirectory.Path()));

		Assert.Contains(".sln", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The regression that started all this. Two solutions share a directory, the smaller one sorts
	/// first, and taking the first by name answered every question in the repository from the wrong
	/// compilation -- returning nothing, which is indistinguishable from a true negative.
	/// </summary>
	[Fact]
	public void Prefers_the_solution_that_compiles_the_file_over_the_first_by_name()
	{
		using var repository = new TwoSolutionRepository();

		var resolved = SolutionResolver.Resolve(Path.Combine(repository.Root, "Wizard", "Thing.cs"));

		Assert.Equal(repository.Main, resolved, ignoreCase: true);
	}

	/// <summary>Containment works from a directory inside the project, not just from a file in it.</summary>
	[Fact]
	public void Prefers_the_containing_solution_for_a_directory_inside_a_project()
	{
		using var repository = new TwoSolutionRepository();

		var resolved = SolutionResolver.Resolve(Path.Combine(repository.Root, "Gather"));

		Assert.Equal(repository.Installer, resolved, ignoreCase: true);
	}

	/// <summary>
	/// A repository root encloses no project, so containment has nothing to say and guessing is what
	/// produced the bug. The candidates go in the message, because the caller can fix the call.
	/// </summary>
	[Fact]
	public void Refuses_to_guess_between_solutions_sharing_a_directory()
	{
		using var repository = new TwoSolutionRepository();

		var error = Assert.Throws<AmbiguousSolutionException>(
			() => SolutionResolver.Resolve(repository.Root));

		Assert.Contains(Path.GetFileName(repository.Main), error.Message, StringComparison.Ordinal);
		Assert.Contains(Path.GetFileName(repository.Installer), error.Message, StringComparison.Ordinal);
		Assert.Contains("rosemcp.json", error.Message, StringComparison.Ordinal);
		Assert.Equal(2, error.Candidates.Count);
	}

	[Fact]
	public void A_pin_beside_them_settles_what_containment_cannot()
	{
		using var repository = new TwoSolutionRepository();
		repository.Pin(Path.GetFileName(repository.Main));

		Assert.Equal(repository.Main, SolutionResolver.Resolve(repository.Root), ignoreCase: true);
	}

	/// <summary>A pin naming something that is not there must not stop the repository working.</summary>
	[Fact]
	public void A_pin_naming_an_absent_solution_is_ignored_rather_than_fatal()
	{
		using var repository = new TwoSolutionRepository();
		repository.Pin("Gone.slnx");

		Assert.Throws<AmbiguousSolutionException>(() => SolutionResolver.Resolve(repository.Root));
	}

	[Fact]
	public void Reports_what_it_chose_between_and_why()
	{
		using var repository = new TwoSolutionRepository();

		var choice = SolutionResolver.Choose(Path.Combine(repository.Root, "Wizard", "Thing.cs"));

		Assert.True(choice.WasContested);
		Assert.Equal(2, choice.Candidates.Count);
		Assert.Contains("compiles", choice.Reason, StringComparison.Ordinal);
	}

	/// <summary>
	/// A change is computed against one solution and written to disk, where a sibling sharing the
	/// project picks the new text up while still calling the old name from projects this solution
	/// never had. The sibling is not stale afterwards; it is broken.
	/// </summary>
	[Fact]
	public void Finds_the_sibling_solution_that_shares_a_changed_file()
	{
		using var repository = new TwoSolutionRepository();
		var changed = Path.Combine(repository.Root, "Gather", "Gather.cs");

		var overlaps = SolutionResolver.SiblingsSharing(repository.Main, [changed]);

		var overlap = Assert.Single(overlaps);
		Assert.Equal(repository.Installer, overlap.SolutionPath, ignoreCase: true);
		Assert.Equal(1, overlap.SharedFileCount);
	}

	[Fact]
	public void Says_nothing_when_no_sibling_shares_the_change()
	{
		using var repository = new TwoSolutionRepository();
		var changed = Path.Combine(repository.Root, "Wizard", "Thing.cs");

		Assert.Empty(SolutionResolver.SiblingsSharing(repository.Main, [changed]));
	}

	[Fact]
	public void An_uncontested_choice_says_so()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var choice = SolutionResolver.Choose(fixture.Path("Simple", "Core", "Calculator.cs"));

		Assert.False(choice.WasContested);
	}

	/// <summary>
	/// Two solutions in one directory, shaped like the repository that produced the bug: a large one
	/// beside a small installer whose name sorts first.
	/// </summary>
	private sealed class TwoSolutionRepository : IDisposable
	{
		public TwoSolutionRepository()
		{
			Root = Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"two-{Guid.NewGuid():N}");

			Project("Wizard");
			Project("Core");
			Project("Gather");

			File.WriteAllText(Path.Combine(Root, "Wizard", "Thing.cs"), "public sealed class Thing;");

			Main = Solution("Repo.slnx", "Wizard", "Core");
			Installer = Solution("Repo.Installer.slnx", "Gather");
		}

		public string Root { get; }

		/// <summary>The one anyone working here means.</summary>
		public string Main { get; }

		/// <summary>Sorts before <see cref="Main"/>, which is the whole point of the fixture.</summary>
		public string Installer { get; }

		public void Pin(string solutionFileName) => File.WriteAllText(
			Path.Combine(Root, "rosemcp.json"),
			$$"""{ "solution": "{{solutionFileName}}" }""");

		public void Dispose()
		{
			try
			{
				Directory.Delete(Root, recursive: true);
			}
			catch (IOException)
			{
				// A temp directory that outlives the run is not worth failing a test over.
			}
		}

		private void Project(string name)
		{
			var directory = Path.Combine(Root, name);
			Directory.CreateDirectory(directory);
			File.WriteAllText(
				Path.Combine(directory, $"{name}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		}

		private string Solution(string fileName, params string[] projects)
		{
			var entries = projects.Select(name => $"  <Project Path=\"{name}/{name}.csproj\" />");
			var path = Path.Combine(Root, fileName);

			File.WriteAllText(path, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>");

			return path;
		}
	}
}
