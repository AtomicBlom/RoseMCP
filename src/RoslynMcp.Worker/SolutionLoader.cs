using System.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

/// <summary>Opens a solution and reports honestly on how well it went.</summary>
public sealed class SolutionLoader(RestoreRunner restoreRunner, ILogger<SolutionLoader> logger)
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

		stopwatch.Stop();

		var report = await WorkspaceStatusReporter.DescribeAsync(
			workspace.CurrentSolution,
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

		return new LoadResult(workspace, report);
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
public sealed record LoadResult(MSBuildWorkspace Workspace, WorkspaceStatusReport Report);
