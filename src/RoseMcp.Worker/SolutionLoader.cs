using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Solutions;
using RoseMcp.Worker.Xaml;

namespace RoseMcp.Worker;

/// <summary>Opens a solution and reports honestly on how well it went.</summary>
public sealed class SolutionLoader(
	RestoreRunner restoreRunner,
	ShadowCopyAnalyzerAssemblyLoader analyzerLoader,
	ILogger<SolutionLoader> logger)
{
	/// <summary>
	/// How the load's own 0-to-100 is divided up. Restore is bounded only by NuGet, and the
	/// design-time build is the part that takes minutes on a large solution, so it gets the bulk.
	/// The shares are guesses, but they are stable guesses: a bar that reaches 70% and then crawls
	/// is more use than one that reaches 99% and stops.
	/// </summary>
	private const double RestoreDone = 10;
	private const double BuildDone = 70;
	private const double AnalyzersDone = 75;
	private const double XamlDone = 80;

	private AnalyzerFileReference? _xamlStubs;

	public async Task<LoadResult> LoadAsync(
		WorkerOptions options,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var stopwatch = Stopwatch.StartNew();

		var projectPaths = SolutionFileReader.ReadProjectPaths(options.SolutionPath);
		var build = BuildProperties.Select(
			options,
			SolutionFileReader.ReadConfigurations(options.SolutionPath),
			WorkspaceConfigFile.Find(options.SolutionPath));

		logger.LogInformation(
			"Opening {SolutionPath} with {ProjectCount} project(s) as {Build}.",
			options.SolutionPath,
			projectPaths.Count,
			build.Describe());

		if (build.Notice is { } notice) logger.LogWarning("{Notice}", notice);

		progress?.Report(
			$"Opening {Path.GetFileName(options.SolutionPath)} as {build.Describe()}, {projectPaths.Count} project(s)", 0);

		var restore = await RestoreAsync(options, build, projectPaths, progress, cancellationToken);

		// The design-time build is a build, so it obeys these exactly as a real one would.
		var workspace = MSBuildWorkspace.Create(
			build.AsGlobalProperties().ToDictionary(property => property.Key, property => property.Value));
		workspace.SkipUnrecognizedProjects = true;
		workspace.LoadMetadataForReferencedProjects = true;

		try
		{
			await OpenAsync(
				workspace,
				options.SolutionPath,
				new ProjectLoadReporter(progress, projectPaths.Count, RestoreDone, BuildDone),
				cancellationToken);
		}
		catch
		{
			workspace.Dispose();
			throw;
		}

		progress?.Report("Redirecting analyzers to shadow copies", BuildDone);
		var solution = UseShadowCopiedAnalyzers(workspace.CurrentSolution);

		if (!options.NoXamlStubs)
		{
			solution = await WithXamlStubsAsync(solution, progress, cancellationToken);
		}

		stopwatch.Stop();

		var report = await WorkspaceStatusReporter.DescribeAsync(
			solution,
			options.SolutionPath,
			workspace.Diagnostics,
			restore,
			revision: 1,
			Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
			cancellationToken,
			progress.Slice(XamlDone, 100),
			build);

		logger.LogInformation(
			"Loaded {SolutionPath} in {Seconds}s: {State}, {ProjectCount} project(s), {GeneratedCount} generated document(s).",
			options.SolutionPath,
			report.LoadSeconds,
			report.State,
			report.Projects.Count,
			report.Projects.Sum(project => project.GeneratedDocumentCount));

		return new LoadResult(workspace, solution, report, build);
	}

	/// <summary>
	/// Restores if it is needed, saying so first. Restore is the one part of a load that reaches
	/// the network, so it is worth naming rather than leaving a bar sitting at nothing.
	/// </summary>
	private async Task<RestoreReport> RestoreAsync(
		WorkerOptions options,
		BuildProperties build,
		IReadOnlyList<string> projectPaths,
		IWorkProgress? progress,
		CancellationToken cancellationToken)
	{
		progress?.Report("Checking restore output", 1);

		var restore = await restoreRunner.EnsureRestoredAsync(
			options.SolutionPath,
			projectPaths,
			options.NoRestore,
			cancellationToken,
			progress.Slice(1, RestoreDone),
			build);

		progress?.Report(restore.Ran ? "Restore finished" : "Restore not needed", RestoreDone);

		return restore;
	}

	/// <summary>
	/// Gives every XAML project the stand-in partials its markup compiler would have written.
	/// <para>
	/// The design-time build reports no XAML items and no additional files, so the markup is found
	/// on disk, added as additional documents -- which the disk synchroniser then watches like any
	/// other tracked file -- and a generator is attached to turn them into source. Skipped entirely
	/// for projects with no XAML, which is most of them.
	/// </para>
	/// </summary>

	/// <summary>
	/// The stub generator's assembly, beside this one. Null, with a warning, when it is missing:
	/// XAML stubbing is an enhancement, and a deployment that dropped one file should degrade to a
	/// workspace without stubs rather than refuse to load the solution at all.
	/// </summary>
	private AnalyzerFileReference? ResolveXamlStubs()
	{
		if (_xamlStubs is not null) return _xamlStubs;

		var path = Path.Combine(AppContext.BaseDirectory, "RoseMcp.XamlStubs.dll");
		if (!File.Exists(path))
		{
			logger.LogWarning("XAML stub generation is unavailable: {Path} is missing.", path);
			return null;
		}

		return _xamlStubs = new AnalyzerFileReference(path, analyzerLoader);
	}
	private async Task<Solution> WithXamlStubsAsync(
		Solution solution,
		IWorkProgress? progress,
		CancellationToken cancellationToken)
	{
		var projects = solution.ProjectIds.ToArray();
		var stubbed = 0;

		// One reference for every XAML project. AnalyzerFileReference is identified by its path, so
		// sharing the instance is what makes the loader shadow-copy the assembly once.
		var reference = ResolveXamlStubs();
		if (reference is null) return solution;

		for (var index = 0; index < projects.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var project = solution.GetProject(projects[index])!;
			if (project.FilePath is not { Length: > 0 } projectFile) continue;

			var markup = XamlItemReader.Read(projectFile);
			if (markup.Count == 0) continue;

			progress?.Report(
				$"Reading XAML: {project.Name} ({markup.Count} file(s))",
				AnalyzersDone + ((XamlDone - AnalyzersDone) * (index + 1) / projects.Length));

			var known = project.AdditionalDocuments
				.Select(document => document.FilePath)
				.Where(path => path is { Length: > 0 })
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (var path in markup.Where(path => !known.Contains(path)))
			{
				var text = await ReadTextAsync(path, cancellationToken);
				if (text is null) continue;

				solution = solution.AddAdditionalDocument(
					DocumentId.CreateNewId(project.Id, Path.GetFileName(path)),
					Path.GetFileName(path),
					text,
					filePath: path);
			}

			solution = solution.AddAnalyzerReference(project.Id, reference);
			stubbed++;
		}

		if (stubbed > 0) logger.LogInformation("Attached XAML stub generation to {ProjectCount} project(s).", stubbed);

		return solution;
	}

	/// <summary>
	/// A file that cannot be read is skipped rather than fatal. Markup being written while we look
	/// is ordinary, and the next read barrier picks it up.
	/// </summary>
	private async Task<Microsoft.CodeAnalysis.Text.SourceText?> ReadTextAsync(
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			return Microsoft.CodeAnalysis.Text.SourceText.From(
				await File.ReadAllTextAsync(path, cancellationToken));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			logger.LogDebug(exception, "Could not read {Path} while looking for XAML.", path);
			return null;
		}
	}
	/// <summary>
	/// Rebuilds every project's analyzer references so they load from throwaway copies.
	/// <para>
	/// Has to happen before anything touches the solution, because the first call that runs a
	/// generator is what loads the assembly and takes the lock. Once that has happened the lock is
	/// held for the life of the process and there is no undoing it.
	/// </para>
	/// </summary>
	private Solution UseShadowCopiedAnalyzers(Solution solution)
	{
		foreach (var projectId in solution.ProjectIds)
		{
			var project = solution.GetProject(projectId)!;
			if (project.AnalyzerReferences.Count == 0) continue;

			var shadowed = project.AnalyzerReferences
				.Select(reference => reference.FullPath is { Length: > 0 } path && File.Exists(path)
					? CreateShadowedReference(path)
					: reference)
				.ToArray();

			solution = solution.GetProject(projectId)!.WithAnalyzerReferences(shadowed).Solution;
		}

		logger.LogDebug("Analyzer assemblies will load from {ShadowDirectory}.", analyzerLoader.ShadowDirectory);

		return solution;
	}

	/// <summary>
	/// Wraps an analyzer path in a reference that loads from a copy, and subscribes to its load
	/// failures. Note the reference keeps the original path: callers check it against disk, and the
	/// redirection to the copy happens inside the loader.
	/// </summary>
	private AnalyzerFileReference CreateShadowedReference(string path)
	{
		var reference = new AnalyzerFileReference(path, analyzerLoader);
		reference.AnalyzerLoadFailed += (_, e) =>
			analyzerLoader.RecordLoadFailure(path, e.Exception?.Message ?? e.Message ?? e.ErrorCode.ToString());

		return reference;
	}

	private static async Task OpenAsync(
		MSBuildWorkspace workspace,
		string path,
		IProgress<ProjectLoadProgress> progress,
		CancellationToken cancellationToken)
	{
		var isProject = Path.GetExtension(path) is ".csproj" or ".vbproj" or ".fsproj";

		if (isProject)
		{
			await workspace.OpenProjectAsync(path, progress, cancellationToken);
			return;
		}

		await workspace.OpenSolutionAsync(path, progress, cancellationToken);
	}

	/// <summary>
	/// Turns Roslyn's per-project load events into one number that only goes up.
	/// <para>
	/// Every project reports evaluate, build and resolve, and a multi-targeted one reports them per
	/// framework, so the only countable milestone is a project file reaching resolve. Counting the
	/// events themselves would run the total past the end on any solution that multi-targets.
	/// </para>
	/// </summary>
	private sealed class ProjectLoadReporter(IWorkProgress? progress, int projectCount, double from, double to)
		: IProgress<ProjectLoadProgress>
	{
		private readonly HashSet<string> _resolved = new(StringComparer.OrdinalIgnoreCase);

		private double? _reachedPercent;

		public void Report(ProjectLoadProgress value)
		{
			if (progress is null) return;

			var name = Path.GetFileNameWithoutExtension(value.FilePath);

			if (value.Operation != ProjectLoadOperation.Resolve)
			{
				// Names what is happening now, carrying the last percentage rather than none, so a
				// project that takes twenty seconds to build is attributable without the bar going
				// blank while it does.
				if (value.Operation == ProjectLoadOperation.Build)
				{
					progress.Report($"Design-time build: {name}", _reachedPercent ?? from);
				}

				return;
			}

			int done;
			lock (_resolved)
			{
				_resolved.Add(value.FilePath);
				done = _resolved.Count;
			}

			var share = projectCount <= 0 ? 1 : Math.Min(1, (double)done / projectCount);
			_reachedPercent = from + ((to - from) * share);

			progress.Report($"Loaded {name} ({done}/{Math.Max(projectCount, done)})", _reachedPercent);
		}
	}
}
