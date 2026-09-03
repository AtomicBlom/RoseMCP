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

	public int Count => Nodes.Count;

	public string? Detail { get; init; }
}
