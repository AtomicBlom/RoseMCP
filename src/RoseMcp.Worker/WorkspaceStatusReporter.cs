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
		BuildProperties? build = null,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader = null)
	{
		var (projects, xamlReports) = await DescribeProjectsAsync(
			solution, analyzerLoader, cancellationToken, progress);

		// Read after the projects are described, not before: describing one is what asks its references
		// for their generators, and that is the moment an assembly that will not load says so.
		var analyzerFailures = analyzerLoader?.LoadFailures ?? [];

		var degradedReasons = (IReadOnlyList<string>)
			[.. CollectDegradedReasons(workspaceDiagnostics, projects, restore, build, analyzerFailures),
			 .. XamlReasons(xamlReports)];

		return new WorkspaceStatusReport
		{
			SolutionPath = solutionPath,
			State = degradedReasons.Count == 0 ? WorkspaceState.Loaded : WorkspaceState.Degraded,
			Revision = revision,
			Projects = projects,
			LoadDiagnostics = LoadDiagnosticSummary.Summarise(workspaceDiagnostics),
			LoadDiagnosticCount = workspaceDiagnostics.Count,
			DegradedReasons = degradedReasons,
			AnalyzerLoadFailures = analyzerFailures,
			BuildConfiguration = build?.Describe(),
			AvailableConfigurations = build?.Available.Configurations ?? [],
			Notices = [.. NoticesFor(build, solution, cancellationToken)],
			Restore = restore,
			LoadSeconds = loadSeconds,
		};
	}

	private static async Task<(IReadOnlyList<ProjectStatus> Statuses, IReadOnlyList<XamlProjectReport> Xaml)> DescribeProjectsAsync(
		Solution solution,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader,
		CancellationToken cancellationToken,
		IWorkProgress? progress)
	{
		var statuses = new List<ProjectStatus>(solution.ProjectIds.Count);
		var xamlReports = new List<XamlProjectReport>();
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
				.SelectMany(reference => SafeGetGenerators(reference, project.Language, analyzerLoader))
				.ToArray();

			// Only pay for generator execution when there is a generator to run.
			var generated = generators.Length == 0
				? []
				: (await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).ToArray();

			var xaml = await XamlStubReportReader.ReadAsync(generated, cancellationToken);
			if (xaml is not null) xamlReports.Add(new XamlProjectReport(project.Name, xaml));

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

		return (statuses, xamlReports);
	}

	/// <summary>One project's XAML stub outcome, carried until every project's is in, so they fold together.</summary>
	public readonly record struct XamlProjectReport(string Project, XamlStubReport Xaml);

	/// <summary>
	/// What is wrong with the solution's XAML stubs, if anything: one reason per kind of wrongness,
	/// naming the projects it covers.
	/// <para>
	/// Successful stubbing is reported in the per-project counts rather than here: it is a caveat
	/// worth seeing, not a reason to call the whole workspace degraded, and marking every XAML
	/// solution degraded would empty that word of meaning. Being unable to stub, or having had to
	/// guess, is a different matter.
	/// </para>
	/// <para>
	/// Folded across projects rather than yielded per project. The three kinds have one cause and one
	/// remedy each, so a per-project list repeats the explanation once per project -- eight paragraphs
	/// on Drawboard's Pdf solution, all of them the same sentence about elements that will not bind.
	/// The types themselves are not repeated here at all: <see cref="ProjectStatus.UnresolvedXamlTypes"/>
	/// already carries every one of them, per project, so naming the projects is enough to reach them.
	/// </para>
	/// </summary>
	public static IEnumerable<string> XamlReasons(IReadOnlyList<XamlProjectReport> reports)
	{
		var undialected = reports.Where(report => report.Xaml.Dialect is null && report.Xaml.MarkupFileCount > 0).ToArray();

		if (undialected.Length > 0)
		{
			yield return $"{Count(undialected.Length, "project has", "projects have")} XAML files that no dialect "
				+ "could be chosen for, so nothing stood in for the markup compiler and their code-behind reports "
				+ $"errors that are not real: {Name(undialected.Select(Describe))}.";
		}

		var ambiguous = reports.Where(report => report.Xaml.DialectAmbiguous).ToArray();

		if (ambiguous.Length > 0)
		{
			yield return $"{Count(ambiguous.Length, "project references", "projects reference")} more than one XAML "
				+ $"framework, so the dialect their stubs were written as is a guess: {Name(ambiguous.Select(Describe))}.";
		}

		var unresolved = reports.Where(report => report.Xaml.UnresolvedTypes.Count > 0).ToArray();

		if (unresolved.Length > 0)
		{
			var total = unresolved.Sum(report => report.Xaml.UnresolvedTypes.Count);

			yield return $"{Count(total, "named XAML element", "named XAML elements")} across "
				+ $"{Count(unresolved.Length, "project", "projects")} have a type the project cannot see, so they "
				+ "have no field and will not bind: "
				+ $"{Name(unresolved.Select(report => $"{report.Project} ({report.Xaml.UnresolvedTypes.Count})"))}. "
				+ "Each project's unresolvedXamlTypes lists them. A project that resolves none of its package types "
				+ "is usually one that was never restored, so check restore before reading these as missing usings.";
		}

		static string Describe(XamlProjectReport report) => $"{report.Project} ({report.Xaml.DialectReason})";
	}

	/// <summary>
	/// The one reason covering every analyzer assembly that would not load, or null when they all did.
	/// <para>
	/// Grouped by assembly, because the failure this catches most often is one generator arriving at
	/// several versions from several target packs and all but one of them losing. Listing the failures
	/// separately buries that: one file name carrying a count is the finding, and nine paragraphs that
	/// each look like an unrelated broken package are not.
	/// </para>
	/// <para>
	/// Public, and taking the failures rather than a loader, for the reason
	/// <see cref="LoadDiagnosticSummary.Fold"/> is: the fold is the part worth testing and it needs no
	/// workspace to exercise.
	/// </para>
	/// </summary>
	public static string? AnalyzerReason(IReadOnlyList<AnalyzerLoadFailure> failures)
	{
		if (failures.Count == 0) return null;

		var assemblies = failures
			.GroupBy(failure => failure.Assembly, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.Count() == 1 ? group.Key : $"{group.Key} (x{group.Count()})");

		return $"{Count(failures.Count, "analyzer assembly", "analyzer assemblies")} failed to load, so the "
			+ "analyzers and source generators inside them are producing nothing while MSBuild still passes them "
			+ $"to the compiler: {Name(assemblies)}. One file name carrying a count is several versions of one "
			+ "generator colliding, and only one of them loaded. Rebuild them, or check their dependencies and "
			+ "the Roslyn version they were built against, then reload. analyzerLoadFailures has each message.";
	}

	/// <summary>
	/// The one reason covering projects left with no restore output, or null when none were.
	/// <para>
	/// Separate from the restore-failed reason and reached only when that one did not fire: a restore
	/// that failed explains itself and its output is the actionable part. This is the other case, and
	/// the one worth having -- restore reporting success while most of the solution is unrestored,
	/// which is what <c>dotnet restore</c> does to a solution of non-SDK projects.
	/// </para>
	/// </summary>
	public static string? UnrestoredReason(RestoreReport? restore)
	{
		if (restore?.Unrestored is not { Count: > 0 } unrestored) return null;

		var lead = restore.Ran ? "Restore reported success, but" : "Restore did not run, and";

		return $"{lead} {Count(unrestored.Count, "project has", "projects have")} no restore output: "
			+ $"{Name(unrestored)}. Their package references resolve to nothing, so the types, analyzers and "
			+ "generators those packages carry are all absent, and the errors that follow describe everything "
			+ "except the cause. 'dotnet restore' passes over projects it does not understand -- non-SDK csproj, "
			+ "which is every UWP one -- and exits 0 regardless; restore those with MSBuild. restore.unrestored "
			+ "lists them.";
	}

	/// <summary>
	/// Names a few of something and says how many were left out, so a folded reason stays actionable
	/// without growing back into the list it replaced. Three, because that is enough to recognise a
	/// family by and short enough to read in a tray card.
	/// </summary>
	private static string Name(IEnumerable<string> items, int show = 3)
	{
		var all = items.ToArray();
		var named = string.Join(", ", all.Take(show));

		return all.Length > show ? $"{named}, and {all.Length - show} more" : named;
	}

	/// <summary>
	/// A count against its noun, both spellings given. English pluralisation is not a suffix rule --
	/// "analyzer assemblies" and "projects have" do not come from adding s -- and a reason that says
	/// "1 projects have" reads as a bug in the thing reporting the bug.
	/// </summary>
	private static string Count(int count, string singular, string plural) =>
		$"{count} {(count == 1 ? singular : plural)}";

	/// <summary>
	/// A generator built against a Roslyn this worker does not have, or one with a broken dependency,
	/// throws on load. That is a fact about the solution rather than a reason to fail the whole open,
	/// so it is recorded beside the failures the reference raises through its own event: a reference
	/// that yields no generators and says nothing is the silent nothing this project exists to catch.
	/// </summary>
	private static IEnumerable<ISourceGenerator> SafeGetGenerators(
		AnalyzerReference reference,
		string language,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader)
	{
		try
		{
			return reference.GetGenerators(language);
		}
		catch (Exception exception)
		{
			analyzerLoader?.RecordLoadFailure(
				reference.FullPath ?? reference.Display ?? language, exception.Message);

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
	/// Null is meant to mean something here: a project that resolved no framework is the signature of
	/// a solution loaded under a configuration it does not declare, the failure that yields thousands
	/// of diagnostics about System.Object being undefined. Anything that makes null common empties
	/// that signal, so this asks three sources before giving up.
	/// </para>
	/// <para>
	/// The build's own value first, which is exact and carries the platform:
	/// <c>net10.0-windows10.0.26100.0</c>. It only reaches the analyzer config when something asked
	/// for it though, so eleven healthy netstandard2.0 projects in one Revit monorepo have
	/// none. Then the preprocessor symbols, which the SDK defines unconditionally and which no
	/// generator has to request. The project name last, and only when it looks like a framework:
	/// Roslyn appends a TFM there solely to tell the targets of a multi-targeted project apart, so a
	/// project merely called <c>Foo (Legacy)</c> would otherwise answer with "Legacy".
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

		if (TargetFrameworkSymbols.Infer(project.ParseOptions?.PreprocessorSymbolNames) is { } inferred) return inferred;

		var open = project.Name.LastIndexOf('(');
		var close = project.Name.LastIndexOf(')');
		if (open < 0 || close < open) return null;

		var named = project.Name[(open + 1)..close];

		return named.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? named : null;
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

	/// <summary>
	/// What is worth saying about a workspace without calling it degraded.
	/// <para>
	/// Stale build output belongs here and not in degradedReasons, and the distinction is the point.
	/// Degraded means these answers cannot be trusted, and they are exactly as good with a stale bin
	/// directory as without one -- this reads source, not assemblies. It is also the ordinary state
	/// of a solution somebody is editing, so putting it in degradedReasons would mark almost every
	/// workspace on the machine degraded: the same emptying of the word that the blanket
	/// MSBuild-failure count below was already narrowed to avoid.
	/// </para>
	/// <para>
	/// It is still worth saying, because the thing it warns about does not present as a build
	/// failure. It presents as a test failing for a reason that has nothing to do with the change.
	/// </para>
	/// </summary>
	private static IEnumerable<string> NoticesFor(
		BuildProperties? build,
		Solution solution,
		CancellationToken cancellationToken)
	{
		if (build?.Notice is { } notice) yield return notice;

		var stale = BuildFreshness.Of(solution, project: null, cancellationToken)
			.Count(project => project.Stale);

		if (stale == 0) yield break;

		yield return $"{stale} project(s) have sources newer than their last build output, so anything run out "
			+ "of bin or obj is not this code. rose_build_freshness says which. This does not make the "
			+ "workspace degraded: these answers come from source.";
	}

	/// <summary>
	/// Whether the platform this server chose looks like the wrong one, asked of the properties that
	/// chose it and answered from what MSBuild then complained about.
	/// </summary>
	private static string? WrongPlatformSuspicion(
		BuildProperties? build,
		IReadOnlyList<WorkspaceDiagnostic> diagnostics) =>
		build?.SuspectWrongPlatform(diagnostics.Select(diagnostic => diagnostic.Message));

	private static IReadOnlyList<string> CollectDegradedReasons(
		IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
		IReadOnlyList<ProjectStatus> projects,
		RestoreReport? restore,
		BuildProperties? build,
		IReadOnlyList<AnalyzerLoadFailure> analyzerLoadFailures)
	{
		var reasons = new List<string>();

		if (restore is { Ran: true, Succeeded: false })
		{
			reasons.Add("Restore failed, so the design-time build could not resolve references or analyzers. "
				+ "Run dotnet restore and inspect the output.");
		}
		else if (UnrestoredReason(restore) is { } unrestored)
		{
			reasons.Add(unrestored);
		}

		if (WrongPlatformSuspicion(build, workspaceDiagnostics) is { } platform) reasons.Add(platform);

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

		// The file being there and not loading, which the missing-output check above cannot see. The
		// compiler is handed the reference either way, so the generators inside it produce nothing while
		// the project reports a clean load and a generator count of zero -- a healthy-looking workspace
		// that is not one, and the second of the three failures this server exists to prevent.
		if (AnalyzerReason(analyzerLoadFailures) is { } analyzers) reasons.Add(analyzers);

		// Counted, but only degrading when something actually came back impaired. MSBuild's Failure
		// kind covers complaints that have no bearing on whether a project compiled, and a status
		// that reads Degraded on every solution on the machine tells a caller nothing it can act on.
		var failures = workspaceDiagnostics.Count(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure);
		var anyImpaired = projects.Any(project => !project.LoadedSuccessfully);

		if (failures > 0 && anyImpaired) reasons.Add($"MSBuild reported {failures} load failure(s); see loadDiagnostics.");

		return reasons;
	}
}
