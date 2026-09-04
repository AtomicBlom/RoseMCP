namespace RoseMcp.Contracts;

/// <summary>
/// A snapshot of a live app's XAML visual tree, taken by injecting the diagnostics provider and
/// enumerating the tree on the app's UI thread. <see cref="Nodes"/> is the flat element list (rebuild
/// the tree from each node's parent and child index). <see cref="Detail"/> is set, with
/// <see cref="Nodes"/> empty, when the tree could not be read -- the target has no XAML UI, the
/// provider is not built for this architecture, or injection failed -- so the caller gets a reason
/// rather than an exception.
/// </summary>
public sealed record LiveXamlTree
{
	public IReadOnlyList<LiveXamlNode> Nodes { get; init; } = [];

	/// <summary>The number of nodes in this page.</summary>
	public int Count => Nodes.Count;

	/// <summary>How many nodes matched before paging, so the caller knows whether more remain.</summary>
	public int Total { get; init; }

	/// <summary>
	/// Where the packaged app being inspected is installed from, for a UWP target. Null otherwise.
	/// <para>
	/// Here as well as on the session because this is the tool that answers plausibly rather than
	/// failing. Every node can carry a source file and line, and if the registration points at a
	/// build other than the one on disk then those locations name files whose current contents no
	/// longer correspond to what is running -- silent, plausible, and wrong. The debugger at least
	/// fails loudly; this does not, so it says where the thing it is describing came from.
	/// </para>
	/// </summary>
	public string? InstallLocation { get; init; }

	public string? Detail { get; init; }
}
