using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

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

	public async Task<LoadResult> LoadAsync(
		WorkerOptions options,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var stopwatch = Stopwatch.StartNew();

		var projectPaths = SolutionFileReader.ReadProjectPaths(options.SolutionPath);
		logger.LogInformation("Opening {SolutionPath} with {ProjectCount} project(s).", options.SolutionPath, projectPaths.Count);

		progress?.Report($"Opening {Path.GetFileName(options.SolutionPath)}, {projectPaths.Count} project(s)", 0);

		var restore = await RestoreAsync(options, projectPaths, progress, cancellationToken);

		var workspace = MSBuildWorkspace.Create();
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

		stopwatch.Stop();

		var report = await WorkspaceStatusReporter.DescribeAsync(
			solution,
			options.SolutionPath,
			workspace.Diagnostics,
			restore,
			revision: 1,
			Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
			cancellationToken,
			progress.Slice(AnalyzersDone, 100));

		logger.LogInformation(
			"Loaded {SolutionPath} in {Seconds}s: {State}, {ProjectCount} project(s), {GeneratedCount} generated document(s).",
			options.SolutionPath,
			report.LoadSeconds,
			report.State,
			report.Projects.Count,
			report.Projects.Sum(project => project.GeneratedDocumentCount));

		return new LoadResult(workspace, solution, report);
	}

	/// <summary>
	/// Restores if it is needed, saying so first. Restore is the one part of a load that reaches
	/// the network, so it is worth naming rather than leaving a bar sitting at nothing.
	/// </summary>
	private async Task<RestoreReport> RestoreAsync(
		WorkerOptions options,
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
			progress.Slice(1, RestoreDone));

		progress?.Report(restore.Ran ? "Restore finished" : "Restore not needed", RestoreDone);

		return restore;
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

		public void Report(ProjectLoadProgress value)
		{
			if (progress is null) return;

			var name = Path.GetFileNameWithoutExtension(value.FilePath);

			if (value.Operation != ProjectLoadOperation.Resolve)
			{
				// Names what is happening now without moving the number, so a project that takes
				// twenty seconds to build is attributable rather than just slow.
				if (value.Operation == ProjectLoadOperation.Build) progress.Report($"Design-time build: {name}");

				return;
			}

			int done;
			lock (_resolved)
			{
				_resolved.Add(value.FilePath);
				done = _resolved.Count;
			}

			var share = projectCount <= 0 ? 1 : Math.Min(1, (double)done / projectCount);
			progress.Report($"Loaded {name} ({done}/{Math.Max(projectCount, done)})", from + ((to - from) * share));
		}
	}
}

/// <summary>The opened workspace and the report describing how it went. The caller owns the workspace.</summary>
public sealed record LoadResult(MSBuildWorkspace Workspace, Solution Solution, WorkspaceStatusReport Report);
