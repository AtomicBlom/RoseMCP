using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// The solution's project graph, which answers two questions nothing else here does: where a new
/// type is allowed to go, and how far a change to a public one reaches.
/// <para>
/// Both are otherwise answered by opening project files and following references by hand, which is
/// slow and gets the transitive half wrong -- and the transitive half is the one that matters,
/// since a member's blast radius is everything that depends on its project rather than everything
/// that names it.
/// </para>
/// </summary>
public static class ProjectGraphService
{
	public static ProjectGraphResult Describe(WorkspaceSnapshot snapshot, string? project)
	{
		var solution = snapshot.Solution;
		var graph = solution.GetProjectDependencyGraph();

		var wanted = project is { Length: > 0 }
			? solution.Projects.Where(candidate =>
				string.Equals(candidate.Name, project, StringComparison.OrdinalIgnoreCase)).ToArray()
			: [.. solution.Projects];

		if (wanted.Length == 0)
		{
			throw new ArgumentException(
				$"No project called '{project}'. The solution has "
					+ $"{string.Join(", ", solution.Projects.Select(candidate => candidate.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}.");
		}

		var described = wanted
			.Select(candidate => Describe(solution, graph, candidate))
			.OrderBy(node => node.Name, StringComparer.Ordinal)
			.ToArray();

		return new ProjectGraphResult
		{
			Revision = snapshot.Revision,
			Projects = described,
			Notices = [.. snapshot.Notices],
		};
	}

	private static ProjectNode Describe(Solution solution, ProjectDependencyGraph graph, Project project) =>
		new()
		{
			Name = project.Name,
			FilePath = project.FilePath,

			// Null rather than a guess where the project is loaded under several frameworks at once:
			// each of those is a separate Roslyn project and this is one of them, so naming one would
			// describe a compilation the caller did not ask about.
			TargetFramework = Framework(solution, project),
			OutputPath = project.OutputFilePath,
			IsTestProject = TestProjects.IsTest(project),
			References = Names(solution, project.ProjectReferences.Select(reference => reference.ProjectId)),
			ReferencedBy = Names(solution, graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id)),
			DocumentCount = project.DocumentIds.Count,
		};

	/// <summary>
	/// The framework a project targets, where its name says so unambiguously. MSBuild loads a
	/// multi-targeted project once per framework and distinguishes them in the project name, so a
	/// name with no framework in it is a project with exactly one and nothing to disambiguate.
	/// </summary>
	private static string? Framework(Solution solution, Project project)
	{
		var siblings = solution.Projects
			.Count(candidate => string.Equals(candidate.FilePath, project.FilePath, StringComparison.OrdinalIgnoreCase));

		if (siblings <= 1) return null;

		var open = project.Name.LastIndexOf('(');

		return open > 0 && project.Name.EndsWith(')')
			? project.Name[(open + 1)..^1]
			: null;
	}

	private static IReadOnlyList<string> Names(Solution solution, IEnumerable<ProjectId> ids) =>
		[
			.. ids
				.Select(solution.GetProject)
				.OfType<Project>()
				.Select(project => project.Name)
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal),
		];
}
