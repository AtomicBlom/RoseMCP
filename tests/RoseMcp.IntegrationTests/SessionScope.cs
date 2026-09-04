using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Keeps a session and the temp fixture it reads alive together, and tears both down in order.
/// <para>
/// Shared rather than nested in one test class, because the tests that reach for a real session
/// rather than <see cref="TestSession"/> are the ones that go on to edit the fixture on disk, and
/// they need the fixture in hand to do it.
/// </para>
/// </summary>
public sealed class SessionScope(FixtureSolution fixture, WorkspaceSession session) : IAsyncDisposable
{
	public FixtureSolution Fixture { get; } = fixture;

	public WorkspaceSession Session { get; } = session;

	public static async Task<SessionScope> OpenAsync(string fixtureName, string solutionFileName)
	{
		var fixture = FixtureSolution.Copy(fixtureName, solutionFileName);

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var options = new WorkerOptions { SolutionPath = fixture.SolutionPath };
		var load = await loader.LoadAsync(options, TestContext.Current.CancellationToken);

		return new SessionScope(
			fixture,
			WorkspaceSession.Create(
				load,
				loader,
				options,
				NullLogger<WorkspaceSession>.Instance,
				NullLogger<SolutionWatcher>.Instance));
	}

	public async ValueTask DisposeAsync()
	{
		await Session.DisposeAsync();
		Fixture.Dispose();
	}
}
