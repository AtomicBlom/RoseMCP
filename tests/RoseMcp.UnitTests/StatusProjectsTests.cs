using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// A status answer for a solution of two hundred projects that all loaded is two hundred entries saying
/// so, and tens of thousands of tokens before the caller reaches the part that matters. The ones with
/// something wrong stay; the rest are counted.
/// </summary>
public sealed class StatusProjectsTests
{
	[Test]
	public void Lists_only_the_projects_with_something_to_say()
	{
		var report = Report(
			Project("Clean"),
			Project("Failed") with { LoadedSuccessfully = false },
			Project("Generators") with { MissingAnalyzerOutputs = ["obj/Generator.dll"] },
			Project("Markup") with { UnresolvedXamlTypes = ["Gone"] },
			Project("AlsoClean"));

		var listed = StatusProjects.Listed(report, all: false);

		listed.Projects.Select(project => project.Name).ShouldBe(["Failed", "Generators", "Markup"]);
		listed.ProjectCount.ShouldBe(5);
		listed.Notices.ShouldBeEmpty();
	}

	[Test]
	public void Lists_every_project_when_asked()
	{
		var report = Report(Project("Clean"), Project("AlsoClean"));

		StatusProjects.Listed(report, all: true).ShouldBeSameAs(report);
	}

	/// <summary>Nothing left out is nothing to say about leaving it out.</summary>
	[Test]
	public void Says_nothing_when_every_project_is_listed_anyway()
	{
		var report = Report(Project("Failed") with { LoadedSuccessfully = false });

		StatusProjects.Listed(report, all: false).ShouldBeSameAs(report);
	}

	private static WorkspaceStatusReport Report(params ProjectStatus[] projects) => new()
	{
		SolutionPath = "C:/repo/Repo.slnx",
		State = WorkspaceState.Loaded,
		Revision = 1,
		Projects = projects,
		ProjectCount = projects.Length,
		LoadDiagnostics = [],
		DegradedReasons = [],
	};

	private static ProjectStatus Project(string name) => new()
	{
		Name = name,
		FilePath = $"C:/repo/{name}/{name}.csproj",
		LoadedSuccessfully = true,
		DocumentCount = 1,
		AdditionalDocumentCount = 0,
		AnalyzerReferenceCount = 0,
		GeneratorCount = 0,
		GeneratedDocumentCount = 0,
		MissingAnalyzerOutputs = [],
	};
}
