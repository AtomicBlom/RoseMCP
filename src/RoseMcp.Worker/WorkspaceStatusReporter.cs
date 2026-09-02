using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

using RoseMcp.Contracts;
using RoseMcp.Worker.Xaml;

namespace RoseMcp.Worker;

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
		CancellationToken cancellationToken,
		IWorkProgress? progress = null,
		BuildProperties? build = null)
	{
		var (projects, xamlReasons) = await DescribeProjectsAsync(
			solution, cancellationToken, progress);

		var degradedReasons = (IReadOnlyList<string>)
			[.. CollectDegradedReasons(workspaceDiagnostics, projects, restore), .. xamlReasons];

		return new WorkspaceStatusReport
		{
			SolutionPath = solutionPath,
			State = degradedReasons.Count == 0 ? WorkspaceState.Loaded : WorkspaceState.Degraded,
			Revision = revision,
			Projects = projects,
			LoadDiagnostics = workspaceDiagnostics.Select(diagnostic => $"[{diagnostic.Kind}] {diagnostic.Message}").ToArray(),
			DegradedReasons = degradedReasons,
			BuildConfiguration = build?.Describe(),
			AvailableConfigurations = build?.Available.Configurations ?? [],
			Notices = build?.Notice is { } notice ? [notice] : [],
			Restore = restore,
			LoadSeconds = loadSeconds,
		};
	}

	private static async Task<(IReadOnlyList<ProjectStatus> Statuses, IReadOnlyList<string> XamlReasons)> DescribeProjectsAsync(
		Solution solution,
		CancellationToken cancellationToken,
		IWorkProgress? progress)
	{
		var statuses = new List<ProjectStatus>(solution.ProjectIds.Count);
		var xamlReasons = new List<string>();
		var total = solution.ProjectIds.Count;

		foreach (var project in solution.Projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Reported before the work rather than after, because running a project's generators is
			// the slow part and naming it afterwards would credit it to the next project along.
			progress?.Report(
				$"Checking generated code: {project.Name} ({statuses.Count + 1}/{total})",
				total == 0 ? 100 : 100.0 * statuses.Count / total);

			var generators = project.AnalyzerReferences
				.SelectMany(reference => SafeGetGenerators(reference, project.Language))
				.ToArray();

			// Only pay for generator execution when there is a generator to run.
			var generated = generators.Length == 0
				? []
				: (await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).ToArray();

			var xaml = await XamlStubReportReader.ReadAsync(generated, cancellationToken);
			if (xaml is not null) xamlReasons.AddRange(XamlConcerns(project.Name, xaml));

			// The report is our own plumbing rather than something the project generates, so it is
			// not counted and, in GeneratedDocumentService, not listed either.
			var generatedCount = generated.Length - (xaml is null ? 0 : 1);

			statuses.Add(new ProjectStatus
			{
				Name = project.Name,
				FilePath = project.FilePath ?? string.Empty,
				TargetFramework = ReadTargetFramework(project),
				LoadedSuccessfully = LoadedSuccessfully(project),
				DocumentCount = project.DocumentIds.Count,
				AdditionalDocumentCount = project.AdditionalDocumentIds.Count,
				AnalyzerReferenceCount = project.AnalyzerReferences.Count,
				GeneratorCount = generators.Length,
				GeneratedDocumentCount = generatedCount,
				MissingAnalyzerOutputs = FindMissingAnalyzerOutputs(project),
				XamlMarkupCount = xaml?.MarkupFileCount ?? 0,
				XamlStubbedCount = xaml?.StubbedClassCount ?? 0,
				XamlDialect = xaml?.Dialect,
				UnresolvedXamlTypes = xaml?.UnresolvedTypes ?? [],
			});
		}

		return (statuses, xamlReasons);
	}

	/// <summary>
	/// What is wrong with a project's XAML stubs, if anything. Successful stubbing is reported in the
	/// per-project counts rather than here: it is a caveat worth seeing, not a reason to call the
	/// whole workspace degraded, and marking every XAML solution degraded would empty that word of
	/// meaning. Being unable to stub, or having had to guess, is a different matter.
	/// </summary>
	private static IEnumerable<string> XamlConcerns(string projectName, XamlStubReport xaml)
	{
		if (xaml.Dialect is null && xaml.MarkupFileCount > 0)
		{
			yield return $"Project {projectName} has {xaml.MarkupFileCount} XAML file(s) but {xaml.DialectReason}, "
				+ "so nothing could stand in for the markup compiler and its code-behind will report errors "
				+ "that are not real.";
		}

		if (xaml.DialectAmbiguous)
		{
			yield return $"Project {projectName} references more than one XAML framework; stubs were written "
				+ $"as {xaml.Dialect} because {xaml.DialectReason}.";
		}

		if (xaml.UnresolvedTypes.Count == 0) yield break;

		var examples = string.Join(", ", xaml.UnresolvedTypes.Take(3));
		var rest = xaml.UnresolvedTypes.Count > 3 ? $", and {xaml.UnresolvedTypes.Count - 3} more" : string.Empty;

		yield return $"Project {projectName} has {xaml.UnresolvedTypes.Count} named XAML element(s) whose type it "
			+ $"cannot see, so they have no field and will not bind: {examples}{rest}.";
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
	/// Whether this project's semantic results can be trusted. Roslyn keeps the real answer to itself
	/// -- Project.HasSuccessfullyLoadedAsync is internal -- so it is inferred from what is public: a
	/// project whose design-time build fell over resolves no metadata references at all, not even the
	/// framework.
	/// <para>
	/// Asked of the compilation, and deliberately not of what MSBuild said about it. MSBuild raises a
	/// <see cref="WorkspaceDiagnosticKind.Failure"/> when NuGet's vulnerability audit cannot reach its
	/// feed, which names every project it could not audit while saying nothing about whether any of
	/// them compiled -- on a machine without that feed, it names all of them. Blaming a project for
	/// appearing in one marked 27 of the 37 projects in Shared.slnx as failed when every one of them
	/// had resolved its references and loaded its documents.
	/// </para>
	/// <para>
	/// A project that resolved no references resolved nothing, and that is the condition worth
	/// reporting. The complaints themselves are not lost: they are in <c>loadDiagnostics</c>.
	/// </para>
	/// </summary>
	private static bool LoadedSuccessfully(Project project) => project.MetadataReferences.Count > 0;

	/// <summary>
	/// The framework a project was actually loaded for.
	/// <para>
	/// Asked of the build first, and only then of the project name. The name carries a TFM solely
	/// when a project multi-targets -- Roslyn disambiguates <c>Core (net10.0)</c> from
	/// <c>Core (net481)</c> -- so reading the name alone reported null for every single-targeted
	/// project in every solution, which is most of them.
	/// </para>
	/// <para>
	/// Null is meant to mean something here. A project with no framework is the signature of a
	/// solution loaded under a configuration it does not declare, which is the failure that yields
	/// thousands of diagnostics about System.Object being undefined. A null that only ever meant
	/// "did not multi-target" was a permanent false alarm sitting on exactly the signal worth
	/// trusting.
	/// </para>
	/// </summary>
	private static string? ReadTargetFramework(Project project)
	{
		var declared = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
		if (declared.TryGetValue("build_property.TargetFramework", out var framework)
			&& !string.IsNullOrWhiteSpace(framework))
		{
			return framework;
		}

		// Older project formats put nothing in the analyzer config. A multi-targeted one still says
		// so in its name, and a single-targeted one genuinely has nothing left to ask.
		var open = project.Name.LastIndexOf('(');
		var close = project.Name.LastIndexOf(')');
		if (open < 0 || close < open) return null;

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

		// Counted, but only degrading when something actually came back impaired. MSBuild's Failure
		// kind covers complaints that have no bearing on whether a project compiled, and a status
		// that reads Degraded on every solution on the machine tells a caller nothing it can act on.
		var failures = workspaceDiagnostics.Count(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure);
		var anyImpaired = projects.Any(project => !project.LoadedSuccessfully);

		if (failures > 0 && anyImpaired)
			reasons.Add($"MSBuild reported {failures} load failure(s); see loadDiagnostics.");

		return reasons;
	}
}
