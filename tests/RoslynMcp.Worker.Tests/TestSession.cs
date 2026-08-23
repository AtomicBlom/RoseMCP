using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynMcp.Worker.Tests;

/// <summary>Builds a WorkspaceSession over a fixture with the noisy plumbing out of the way.</summary>
public static class TestSession
{
	public static async Task<WorkspaceSession> OpenAsync(
		FixtureSolution fixture,
		TimeSpan? unloadGrace = null,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader = null)
	{
		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			analyzerLoader ?? new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var options = new WorkerOptions
		{
			SolutionPath = fixture.SolutionPath,
			UnloadGracePeriod = unloadGrace ?? TimeSpan.FromSeconds(30),
		};

		var load = await loader.LoadAsync(options, TestContext.Current.CancellationToken);

		return WorkspaceSession.Create(
			load,
			loader,
			options,
			NullLogger<WorkspaceSession>.Instance,
			NullLogger<SolutionWatcher>.Instance);
	}
}
