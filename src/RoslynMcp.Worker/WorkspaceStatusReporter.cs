using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

/// <summary>
/// Describes a solution snapshot: what loaded, what generators produced, and any reason the
/// answers cannot be trusted.
/// <para>
/// Kept separate from loading so status reflects the snapshot as it is now, not as it was when the
/// solution was first opened. After an hour of edits those are not the same thing.
/// </para>
/// </summary>
public static class WorkspaceStatusReporter
{
	public static async Task<WorkspaceStatusReport> DescribeAsync(
		Solution solution,
		string solutionPath,
		IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
		RestoreReport? restore,
		long revision,
		double loadSeconds,
		CancellationToken cancellationToken)
	{
		var failureMessages = workspaceDiagnostics
			.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
			.Select(diagnostic => diagnostic.Message)
			.ToArray();

		var projects = await DescribeProjectsAsync(solution, failureMessages, cancellationToken);
		var degradedReasons = CollectDegradedReasons(workspaceDiagnostics, projects, restore);

		return new WorkspaceStatusReport
		{
			SolutionPath = solutionPath,
			State = degradedReasons.Count == 0 ? WorkspaceState.Loaded : WorkspaceState.Degraded,
			Revision = revision,
			Projects = projects,
			LoadDiagnostics = workspaceDiagnostics.Select(diagnostic => $"[{diagnostic.Kind}] {diagnostic.Message}").ToArray(),
			DegradedReasons = degradedReasons,
			Restore = restore,
			LoadSeconds = loadSeconds,
		};
	}

	private static async Task<IReadOnlyList<ProjectStatus>> DescribeProjectsAsync(
		Solution solution,
		IReadOnlyList<string> failureMessages,
		CancellationToken cancellationToken)
	{
		var statuses = new List<ProjectStatus>(solution.ProjectIds.Count);

		foreach (var project in solution.Projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var generators = project.AnalyzerReferences
				.SelectMany(reference => SafeGetGenerators(reference, project.Language))
				.ToArray();

			// Only pay for generator execution when there is a generator to run.
			var generatedCount = generators.Length == 0
				? 0
				: (await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).Count();

			statuses.Add(new ProjectStatus
			{
				Name = project.Name,
				FilePath = project.FilePath ?? string.Empty,
				TargetFramework = ReadTargetFramework(project),
				LoadedSuccessfully = LoadedSuccessfully(project, failureMessages),
				DocumentCount = project.DocumentIds.Count,
				AdditionalDocumentCount = project.AdditionalDocumentIds.Count,
				AnalyzerReferenceCount = project.AnalyzerReferences.Count,
				GeneratorCount = generators.Length,
				GeneratedDocumentCount = generatedCount,
				MissingAnalyzerOutputs = FindMissingAnalyzerOutputs(project),
			});
		}

		return statuses;
	}

	/// <summary>
	/// A generator built against an older Roslyn, or one with a broken dependency, throws on load.
	/// That is a fact about the solution, not a reason to fail the whole open.
	/// </summary>
	private static IEnumerable<ISourceGenerator> SafeGetGenerators(AnalyzerReference reference, string language)
	{
		try
		{
			return reference.GetGenerators(language);
		}
		catch (Exception)
		{
			return [];
		}
	}

	/// <summary>
	/// Whether this project's design-time build worked. Roslyn keeps the real answer to itself --
	/// Project.HasSuccessfullyLoadedAsync is internal -- so infer it from what is public. MSBuild
	/// names the offending file in its failure diagnostics, and a project whose build fell over
	/// resolves no metadata references at all, not even the framework.
	/// </summary>
	private static bool LoadedSuccessfully(Project project, IReadOnlyList<string> failureMessages)
	{
		var blamed = project.FilePath is { Length: > 0 } path
			&& failureMessages.Any(message => message.Contains(path, StringComparison.OrdinalIgnoreCase));

		if (blamed)
			return false;

		return project.MetadataReferences.Count > 0;
	}

	private static string? ReadTargetFramework(Project project)
	{
		// Roslyn appends the TFM to the project name when a project multi-targets: Core (net10.0).
		var open = project.Name.LastIndexOf('(');
		var close = project.Name.LastIndexOf(')');
		if (open < 0 || close < open)
			return null;

		return project.Name[(open + 1)..close];
	}

	/// <summary>
	/// Names of analyzer assemblies this project expects but which are not on disk.
	/// <para>
	/// Checking the file rather than the reference list is the whole point. MSBuild puts the
	/// expected output of an unbuilt in-solution generator on the /analyzer: line regardless, so
	/// the reference is present and only the file is missing. Roslyn loads no generators from it
	/// and reports no error, which is exactly how this failure stays invisible.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> FindMissingAnalyzerOutputs(Project project)
	{
		return project.AnalyzerReferences
			.Select(reference => reference.FullPath)
			.Where(path => !string.IsNullOrEmpty(path) && !File.Exists(path))
			.Select(Path.GetFileNameWithoutExtension)
			.OfType<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static IReadOnlyList<string> CollectDegradedReasons(
		IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
		IReadOnlyList<ProjectStatus> projects,
		RestoreReport? restore)
	{
		var reasons = new List<string>();

		if (restore is { Ran: true, Succeeded: false })
		{
			reasons.Add("Restore failed, so the design-time build could not resolve references or analyzers. "
				+ "Run dotnet restore and inspect the output.");
		}

		foreach (var project in projects.Where(project => !project.LoadedSuccessfully))
		{
			reasons.Add($"Project {project.Name} did not load successfully; its semantic results are unreliable.");
		}

		foreach (var project in projects.Where(project => project.MissingAnalyzerOutputs.Count > 0))
		{
			var analyzerProjects = string.IsNullOrEmpty(project.FilePath)
				? new Dictionary<string, string>()
				: AnalyzerProjectReferences.ReadAnalyzerProjects(project.FilePath);

			foreach (var missing in project.MissingAnalyzerOutputs)
			{
				var remedy = analyzerProjects.TryGetValue(missing, out var owner)
					? $"Build it with: dotnet build {owner}"
					: "Restore or build the solution so the analyzer assembly exists, then reload.";

				reasons.Add($"Project {project.Name} expects analyzer assembly {missing}, but the file is not on "
					+ "disk. MSBuild still passes it to the compiler, so any source generators it contains are "
					+ $"silently producing nothing. {remedy}");
			}
		}

		var failures = workspaceDiagnostics.Count(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure);
		if (failures > 0)
			reasons.Add($"MSBuild reported {failures} load failure(s); see loadDiagnostics.");

		return reasons;
	}
}
