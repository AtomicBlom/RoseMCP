using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>One part of a workspace's load as its opened row shows it on request: a heading and its lines, each as written.</summary>
/// <param name="Title">What the part is.</param>
/// <param name="Lines">Its lines.</param>
public sealed record LoadSection(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// Why a workspace is in the state it is, read from its full status when somebody asks rather than polled: the load
/// itself, the projects that failed by name, the analyzers that would not load in the loader's own words, the
/// projects MSBuild could not evaluate, the restore, and every project's own figures.
/// <para>
/// Verbatim where the worker wrote words, because a person fixing a load needs the message the tools gave, not a
/// paraphrase of it; and asked for rather than polled, because a degraded solution's status is the longest thing a
/// worker says, and reading it every second would be reading it for nobody.
/// </para>
/// </summary>
public static class LoadDetail
{
	/// <summary>The sections of <paramref name="status"/> worth showing, in the order a person fixing a load reads them.</summary>
	public static IReadOnlyList<LoadSection> Of(WorkspaceStatusReport status)
	{
		var sections = new List<LoadSection>();

		var load = new List<string>
		{
			$"{status.State.ToString().ToLowerInvariant()} at revision {status.Revision}",
			$"{Format.Count(status.ProjectCount > 0 ? status.ProjectCount : status.Projects.Count, "project")} in {status.LoadSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s",
		};
		if (status.BuildConfiguration is { Length: > 0 } configuration) load.Add($"built as {configuration}");
		if (status.AvailableConfigurations.Count > 0) load.Add($"the solution declares {string.Join(", ", status.AvailableConfigurations)}");
		if (status.LoadDiagnosticCount > 0) load.Add($"{Format.Count(status.LoadDiagnosticCount, "load diagnostic")} from MSBuild");
		sections.Add(new LoadSection("The load", load));

		if (status.DegradedReasons.Count > 0) sections.Add(new LoadSection("Why it is degraded", status.DegradedReasons));

		var failed = status.Projects.Where(project => !project.LoadedSuccessfully).ToList();
		if (failed.Count > 0)
		{
			sections.Add(new LoadSection(
				$"{Format.Count(failed.Count, "project")} that failed to load",
				[.. failed.Select(project => $"{project.Name}{Framework(project)}{Format.Separator}{project.FilePath}")]));
		}

		if (status.AnalyzerLoadFailures.Count > 0)
		{
			sections.Add(new LoadSection(
				"Analyzers that would not load",
				[.. status.AnalyzerLoadFailures.Select(failure => $"{failure.Assembly}: {failure.Message}")]));
		}

		if (status.EvaluationFailures.Count > 0)
		{
			sections.Add(new LoadSection(
				"Projects MSBuild could not evaluate",
				[.. status.EvaluationFailures.Select(failure =>
					failure.NamesSdk ? $"{failure.Project}: {failure.Message} (an SDK this machine does not have)" : $"{failure.Project}: {failure.Message}")]));
		}

		if (status.Restore is { } restore) sections.Add(new LoadSection("Restore", Restore(restore)));

		if (status.Notices.Count > 0) sections.Add(new LoadSection("Notices", status.Notices));

		if (status.LoadDiagnostics.Count > 0) sections.Add(new LoadSection("What MSBuild said", status.LoadDiagnostics));

		if (status.Projects.Count > 0)
		{
			sections.Add(new LoadSection("Projects", [.. status.Projects.Select(Describe)]));
		}

		return sections;
	}

	/// <summary>One project's own figures on one line: what it targets, whether it loaded, and what it holds.</summary>
	public static string Describe(ProjectStatus project)
	{
		var facts = new List<string>
		{
			project.LoadedSuccessfully ? "loaded" : "failed to load",
			Format.Count(project.DocumentCount, "document"),
		};

		if (project.AdditionalDocumentCount > 0) facts.Add(Format.Count(project.AdditionalDocumentCount, "additional file"));
		if (project.AnalyzerReferenceCount > 0) facts.Add(Format.Count(project.AnalyzerReferenceCount, "analyzer reference"));
		if (project.GeneratorCount > 0)
		{
			facts.Add($"{Format.Count(project.GeneratorCount, "generator")} making {Format.Count(project.GeneratedDocumentCount, "file")}");
		}

		if (project.MissingAnalyzerOutputs.Count > 0) facts.Add($"no output from {string.Join(", ", project.MissingAnalyzerOutputs)}");

		if (project.XamlMarkupCount > 0)
		{
			var dialect = project.XamlDialect is { Length: > 0 } named ? $"{named} " : string.Empty;
			facts.Add($"{Format.Count(project.XamlMarkupCount, $"{dialect}XAML file")}, {project.XamlStubbedCount} stubbed");
		}

		if (project.UnresolvedXamlTypes.Count > 0) facts.Add($"XAML types not found: {string.Join(", ", project.UnresolvedXamlTypes)}");

		return $"{project.Name}{Framework(project)}: {string.Join(", ", facts)}{Format.Separator}{project.FilePath}";
	}

	private static string Framework(ProjectStatus project) =>
		project.TargetFramework is { Length: > 0 } framework ? $" ({framework})" : string.Empty;

	private static IReadOnlyList<string> Restore(RestoreReport restore)
	{
		var lines = new List<string>
		{
			restore.Ran
				? restore.Succeeded == true ? $"Ran and succeeded: {restore.Reason}" : $"Ran and failed: {restore.Reason}"
				: $"Did not run: {restore.Reason}",
		};

		if (restore.Unrestored.Count > 0) lines.Add($"Not restored: {string.Join(", ", restore.Unrestored)}");
		if (restore.Output is { Length: > 0 } output) lines.Add(output);

		return lines;
	}
}
