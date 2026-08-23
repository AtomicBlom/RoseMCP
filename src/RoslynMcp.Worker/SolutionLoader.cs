using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

/// <summary>Opens a solution and reports honestly on how well it went.</summary>
public sealed class SolutionLoader(
	RestoreRunner restoreRunner,
	ShadowCopyAnalyzerAssemblyLoader analyzerLoader,
	ILogger<SolutionLoader> logger)
{
	public async Task<LoadResult> LoadAsync(WorkerOptions options, CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();

		var projectPaths = SolutionFileReader.ReadProjectPaths(options.SolutionPath);
		logger.LogInformation("Opening {SolutionPath} with {ProjectCount} project(s).", options.SolutionPath, projectPaths.Count);

		var restore = await restoreRunner.EnsureRestoredAsync(
			options.SolutionPath, projectPaths, options.NoRestore, cancellationToken);

		var workspace = MSBuildWorkspace.Create();
		workspace.SkipUnrecognizedProjects = true;
		workspace.LoadMetadataForReferencedProjects = true;

		try
		{
			await OpenAsync(workspace, options.SolutionPath, cancellationToken);
		}
		catch
		{
			workspace.Dispose();
			throw;
		}

		var solution = UseShadowCopiedAnalyzers(workspace.CurrentSolution);

		stopwatch.Stop();

		var report = await WorkspaceStatusReporter.DescribeAsync(
			solution,
			options.SolutionPath,
			workspace.Diagnostics,
			restore,
			revision: 1,
			Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
			cancellationToken);

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

	private static async Task OpenAsync(MSBuildWorkspace workspace, string path, CancellationToken cancellationToken)
	{
		var isProject = Path.GetExtension(path) is ".csproj" or ".vbproj" or ".fsproj";

		if (isProject)
		{
			await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken);
			return;
		}

		await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken);
	}
}

/// <summary>The opened workspace and the report describing how it went. The caller owns the workspace.</summary>
public sealed record LoadResult(MSBuildWorkspace Workspace, Solution Solution, WorkspaceStatusReport Report);
