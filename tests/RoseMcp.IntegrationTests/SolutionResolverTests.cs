using RoseMcp.Broker;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Agents refer to code by whatever file they are looking at, not by solution path, so resolution
/// has to work from anywhere inside the tree. Getting this wrong pushes the search onto the caller.
/// </summary>
public sealed class SolutionResolverTests
{
	[Test]
	public void Resolves_a_source_file_to_its_enclosing_solution()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var resolved = SolutionResolver.Resolve(fixture.Path("Simple", "Core", "Calculator.cs"));

		resolved.ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	[Test]
	public void Resolves_a_directory_to_its_enclosing_solution()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var resolved = SolutionResolver.Resolve(fixture.Path("Simple", "App"));

		resolved.ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	[Test]
	public void Passes_a_solution_path_straight_through()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		SolutionResolver.Resolve(fixture.SolutionPath).ShouldBe(fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>A project with no solution above it is still perfectly loadable.</summary>
	[Test]
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
			SolutionResolver.Resolve(project).ShouldBe(project, StringCompareShould.IgnoreCase);
			SolutionResolver.Resolve(Path.Combine(projectDirectory, "Thing.cs")).ShouldBe(project, StringCompareShould.IgnoreCase);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void Says_what_to_pass_when_nothing_can_be_resolved()
	{
		var error = Should.Throw<ArgumentException>(() => SolutionResolver.Resolve(NowhereDirectory.Path())).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain(".sln", Case.Sensitive);
	}

	/// <summary>
	/// The regression that started all this. Two solutions share a directory, the smaller one sorts
	/// first, and taking the first by name answered every question in the repository from the wrong
	/// compilation -- returning nothing, which is indistinguishable from a true negative.
	/// </summary>
	[Test]
	public void Prefers_the_solution_that_compiles_the_file_over_the_first_by_name()
	{
		using var repository = new TwoSolutionRepository();

		var resolved = SolutionResolver.Resolve(Path.Combine(repository.Root, "Wizard", "Thing.cs"));

		resolved.ShouldBe(repository.Main, StringCompareShould.IgnoreCase);
	}

	/// <summary>Containment works from a directory inside the project, not just from a file in it.</summary>
	[Test]
	public void Prefers_the_containing_solution_for_a_directory_inside_a_project()
	{
		using var repository = new TwoSolutionRepository();

		var resolved = SolutionResolver.Resolve(Path.Combine(repository.Root, "Gather"));

		resolved.ShouldBe(repository.Installer, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A repository root encloses no project, so containment has nothing to say and guessing is what
	/// produced the bug. The candidates go in the message, because the caller can fix the call.
	/// </summary>
	[Test]
	public void Refuses_to_guess_between_solutions_sharing_a_directory()
	{
		using var repository = new TwoSolutionRepository();

		var error = Should.Throw<AmbiguousSolutionException>(
			() => SolutionResolver.Resolve(repository.Root)).ShouldBeOfType<AmbiguousSolutionException>();

		error.Message.ShouldContain(Path.GetFileName(repository.Main), Case.Sensitive);
		error.Message.ShouldContain(Path.GetFileName(repository.Installer), Case.Sensitive);
		error.Message.ShouldContain("rosemcp.json", Case.Sensitive);
		error.Candidates.Count.ShouldBe(2);
	}

	[Test]
	public void A_pin_beside_them_settles_what_containment_cannot()
	{
		using var repository = new TwoSolutionRepository();
		repository.Pin(Path.GetFileName(repository.Main));

		SolutionResolver.Resolve(repository.Root).ShouldBe(repository.Main, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// The other half of that sentence, and the half nothing used to assert. A pin is the directory's
	/// default; containment is evidence about the path in hand, and evidence wins.
	/// <para>
	/// This is not a hypothetical ordering. The pin is the fix the ambiguity error recommends, and
	/// the repository that provoked it holds three solutions of which the largest omits 28 projects
	/// -- so under the old order, taking that advice pointed every question about those projects at a
	/// compilation that does not contain the file.
	/// </para>
	/// </summary>
	[Test]
	public void Containment_beats_a_pin_that_does_not_compile_the_path()
	{
		using var repository = new TwoSolutionRepository();

		// Pinned to the solution that does not contain Wizard.
		repository.Pin(Path.GetFileName(repository.Installer));

		var choice = SolutionResolver.Choose(Path.Combine(repository.Root, "Wizard", "Thing.cs"));

		choice.SolutionPath.ShouldBe(repository.Main, StringCompareShould.IgnoreCase);
		choice.Reason.ShouldContain("compiles", Case.Sensitive);
	}

	/// <summary>A pin naming something that is not there must not stop the repository working.</summary>
	[Test]
	public void A_pin_naming_an_absent_solution_is_ignored_rather_than_fatal()
	{
		using var repository = new TwoSolutionRepository();
		repository.Pin("Gone.slnx");

		Should.Throw<AmbiguousSolutionException>(() => SolutionResolver.Resolve(repository.Root)).ShouldBeOfType<AmbiguousSolutionException>();
	}

	[Test]
	public void Reports_what_it_chose_between_and_why()
	{
		using var repository = new TwoSolutionRepository();

		var choice = SolutionResolver.Choose(Path.Combine(repository.Root, "Wizard", "Thing.cs"));

		choice.WasContested.ShouldBeTrue();
		choice.Candidates.Count.ShouldBe(2);
		choice.Reason.ShouldContain("compiles", Case.Sensitive);
	}

	/// <summary>
	/// A change is computed against one solution and written to disk, where a sibling sharing the
	/// project picks the new text up while still calling the old name from projects this solution
	/// never had. The sibling is not stale afterwards; it is broken.
	/// </summary>
	[Test]
	public void Finds_the_sibling_solution_that_shares_a_changed_file()
	{
		using var repository = new TwoSolutionRepository();
		var changed = Path.Combine(repository.Root, "Gather", "Gather.cs");

		var overlaps = SolutionResolver.SiblingsSharing(repository.Main, [changed]);

		var overlap = overlaps.ShouldHaveSingleItem();
		overlap.SolutionPath.ShouldBe(repository.Installer, StringCompareShould.IgnoreCase);
		overlap.SharedFileCount.ShouldBe(1);
	}

	[Test]
	public void Says_nothing_when_no_sibling_shares_the_change()
	{
		using var repository = new TwoSolutionRepository();
		var changed = Path.Combine(repository.Root, "Wizard", "Thing.cs");

		SolutionResolver.SiblingsSharing(repository.Main, [changed]).ShouldBeEmpty();
	}

	/// <summary>
	/// A sibling made of a subset of the loaded solution's projects compiles the changed file too, but
	/// every reference it could hold was in the compilation the change ran against. Naming it is a
	/// warning on every edit with nothing for the caller to finish.
	/// </summary>
	[Test]
	public void Says_nothing_about_a_sibling_whose_projects_the_loaded_solution_all_has()
	{
		using var repository = new TwoSolutionRepository();
		repository.Solution("Repo.Core.slnx", "Core");
		var changed = Path.Combine(repository.Root, "Core", "Core.cs");

		SolutionResolver.SiblingsSharing(repository.Main, [changed]).ShouldBeEmpty();
	}

	[Test]
	public void An_uncontested_choice_says_so()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var choice = SolutionResolver.Choose(fixture.Path("Simple", "Core", "Calculator.cs"));

		choice.WasContested.ShouldBeFalse("one candidate is no contest");
	}

	/// <summary>
	/// A repository pins its everything-solution at the root and keeps a smaller solution in a
	/// subfolder. A file under that subfolder is compiled by both, and the walk up from it finds the
	/// small one first -- so without reading the pin above, the root's committed default never decides
	/// anything under a folder holding a solution of its own.
	/// </summary>
	[Test]
	public void A_pin_above_the_nearest_solution_decides_a_path_both_compile()
	{
		using var repository = new NestedRepository();

		var choice = SolutionResolver.Choose(Path.Combine(repository.Root, "Tools", "Shared", "Thing.cs"));

		choice.SolutionPath.ShouldBe(repository.Everything, StringCompareShould.IgnoreCase);
		choice.Reason.ShouldContain("pinned by", Case.Sensitive);
		choice.Candidates.ShouldContain(repository.Area);
	}

	/// <summary>
	/// Containment still comes first: a project only the nearer solution compiles is answered by it, since
	/// a pinned solution without the file is no answer about it.
	/// </summary>
	[Test]
	public void The_nearer_solution_stands_where_the_pinned_one_does_not_compile_the_path()
	{
		using var repository = new NestedRepository();

		var choice = SolutionResolver.Choose(Path.Combine(repository.Root, "Tools", "Runner", "Main.cs"));

		choice.SolutionPath.ShouldBe(repository.Area, StringCompareShould.IgnoreCase);
	}

	/// <summary>The nearest pin governs, so a subfolder can pin its own solution under a root that pins another.</summary>
	[Test]
	public void A_nearer_pin_governs_its_own_folder()
	{
		using var repository = new NestedRepository();
		repository.PinArea();

		var choice = SolutionResolver.Choose(Path.Combine(repository.Root, "Tools", "Shared", "Thing.cs"));

		choice.SolutionPath.ShouldBe(repository.Area, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A root solution pinned in the root's rosemcp.json, and a smaller solution in a subfolder sharing one
	/// of its projects and holding one of its own -- the shape of a repository with an everything-solution
	/// beside per-area ones.
	/// </summary>
	private sealed class NestedRepository : IDisposable
	{
		public NestedRepository()
		{
			Root = Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"nested-{Guid.NewGuid():N}");

			Project("Core");
			Project(Path.Combine("Tools", "Shared"));
			Project(Path.Combine("Tools", "Runner"));

			Everything = Solution("Repo.slnx", "Core/Core.csproj", "Tools/Shared/Shared.csproj");
			Area = Solution(Path.Combine("Tools", "Tools.slnx"), "Shared/Shared.csproj", "Runner/Runner.csproj");

			File.WriteAllText(Path.Combine(Root, "rosemcp.json"), """{ "solution": "Repo.slnx" }""");
		}

		public string Root { get; }

		/// <summary>The everything-solution, pinned at the root.</summary>
		public string Everything { get; }

		/// <summary>The smaller solution in a subfolder, nearer to everything under it.</summary>
		public string Area { get; }

		public void PinArea() => File.WriteAllText(
			Path.Combine(Root, "Tools", "rosemcp.json"), """{ "solution": "Tools.slnx" }""");

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

		private void Project(string relative)
		{
			var directory = Path.Combine(Root, relative);
			Directory.CreateDirectory(directory);
			File.WriteAllText(
				Path.Combine(directory, $"{Path.GetFileName(relative)}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		}

		private string Solution(string relative, params string[] projects)
		{
			var entries = projects.Select(project => $"  <Project Path=\"{project}\" />");
			var path = Path.Combine(Root, relative);

			File.WriteAllText(path, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>");

			return path;
		}
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

		public string Solution(string fileName, params string[] projects)
		{
			var entries = projects.Select(name => $"  <Project Path=\"{name}/{name}.csproj\" />");
			var path = Path.Combine(Root, fileName);

			File.WriteAllText(path, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>");

			return path;
		}
	}
}
