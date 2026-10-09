using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Which projects a status answer lists: the ones with something to say, unless the caller asked for
/// every one.
/// <para>
/// A project that failed to load is always listed, so the broker's own copy of the last status still
/// names every one; it reads the total from the count rather than from the list.
/// </para>
/// </summary>
public static class StatusProjects
{
	/// <summary>
	/// The report with only the projects worth reading in it, or the report as it is where
	/// <paramref name="all"/> asks for every project. What was left out is said by
	/// <see cref="WorkspaceStatusReport.ProjectCount"/> rather than by a notice, because the broker keeps
	/// this report and its notices travel on into answers that never had an includeProjects to pass.
	/// </summary>
	public static WorkspaceStatusReport Listed(WorkspaceStatusReport report, bool all)
	{
		if (all) return report;

		var worthReading = report.Projects.Where(SaysSomething).ToList();

		return worthReading.Count == report.Projects.Count ? report : report with { Projects = worthReading };
	}

	/// <summary>Whether a project's entry says anything beyond that it loaded.</summary>
	private static bool SaysSomething(ProjectStatus project) =>
		!project.LoadedSuccessfully
		|| project.MissingAnalyzerOutputs.Count > 0
		|| project.UnresolvedXamlTypes.Count > 0;
}
