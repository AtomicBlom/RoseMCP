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
	/// <summary>Members.slnx, the fixture the outline, navigation, name and island reads ask about most.</summary>
	public SharedWorkspace Members { get; } = new("Members", "Members.slnx");

	/// <summary>Simple.sln, two projects with one reference between them.</summary>
	public SharedWorkspace Simple { get; } = new("Simple", "Simple.sln");

	/// <summary>MultiType.slnx, several types to a file, which the implementation reads walk.</summary>
	public SharedWorkspace MultiType { get; } = new("MultiType", "MultiType.slnx");

	/// <summary>Hierarchy.slnx, a type hierarchy spread across projects, one file compiled by two of them.</summary>
	public SharedWorkspace Hierarchy { get; } = new("Hierarchy", "Hierarchy.slnx");

	/// <summary>XamlStub.slnx, a XAML project whose code-behind binds only through the generated stub.</summary>
	public SharedWorkspace XamlStub { get; } = new("XamlStub", "XamlStub.slnx");

	public async ValueTask DisposeAsync()
	{
		// Every workspace is attempted whatever the others do, so one that fails to close cannot leave the
		// rest loaded and their copies on disk. The first failure is the one reported.
		var failures = new List<Exception>();
		foreach (var workspace in (SharedWorkspace[])[Members, Simple, MultiType, Hierarchy, XamlStub])
		{
			try
			{
				await workspace.DisposeAsync();
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}
		}

		if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
		if (failures.Count > 1) throw new AggregateException("More than one shared workspace failed to close.", failures);
	}
}
