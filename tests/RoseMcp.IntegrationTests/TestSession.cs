using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Builds a WorkspaceSession over a fixture with the noisy plumbing out of the way.
/// <para>
/// A session of the test's own, which is a design-time build of the test's own: the expensive path,
/// and the right one only for a test that writes -- through a tool, or to the fixture on disk -- or
/// that needs the load itself to be different. A test that only reads takes the fixture's
/// <see cref="SharedWorkspace"/> from <see cref="SharedWorkspaces"/> instead, which is loaded once
/// for the whole run.
/// </para>
/// </summary>
public static class TestSession
{
	public static Task<WorkspaceSession> OpenAsync(
		FixtureSolution fixture,
		TimeSpan? unloadGrace = null,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader = null) =>
		OpenAsync(fixture, unloadGrace, analyzerLoader, TestContext.Current!.Execution.CancellationToken);

	/// <summary>
	/// The same, cancelled by <paramref name="cancellationToken"/> rather than by the calling test, for
	/// a load that belongs to no single test.
	/// </summary>
	internal static async Task<WorkspaceSession> OpenAsync(
		FixtureSolution fixture,
		TimeSpan? unloadGrace,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader,
		CancellationToken cancellationToken)
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

		var load = await loader.LoadAsync(options, cancellationToken);

		return WorkspaceSession.Create(
			load,
			loader,
			options,
			NullLogger<WorkspaceSession>.Instance,
			NullLogger<SolutionWatcher>.Instance);
	}
}
