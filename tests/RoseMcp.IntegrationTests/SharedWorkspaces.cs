namespace RoseMcp.IntegrationTests;

/// <summary>
/// The fixtures a test can read without loading one of its own, each loaded at most once a run.
/// <para>
/// A class takes these as the probe-app classes take their app,
/// <c>[ClassDataSource&lt;SharedWorkspaces&gt;(Shared = SharedType.PerAssembly)]</c> and a
/// constructor parameter, and a test reads <c>workspaces.Members</c> rather than copying and loading
/// <c>Members</c> itself. A fixture is here because tests read it often enough for its load to be
/// worth sharing; one read by a single test gains nothing from being here.
/// </para>
/// </summary>
public sealed class SharedWorkspaces : IAsyncDisposable
{
	public SharedWorkspace Members { get; } = new("Members", "Members.slnx");

	public SharedWorkspace Simple { get; } = new("Simple", "Simple.sln");

	public SharedWorkspace MultiType { get; } = new("MultiType", "MultiType.slnx");

	public SharedWorkspace Hierarchy { get; } = new("Hierarchy", "Hierarchy.slnx");

	public SharedWorkspace XamlStub { get; } = new("XamlStub", "XamlStub.slnx");

	public async ValueTask DisposeAsync()
	{
		await Members.DisposeAsync();
		await Simple.DisposeAsync();
		await MultiType.DisposeAsync();
		await Hierarchy.DisposeAsync();
		await XamlStub.DisposeAsync();
	}
}
