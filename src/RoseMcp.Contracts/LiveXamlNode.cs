namespace RoseMcp.Contracts;

/// <summary>
/// One element of a live XAML visual tree, as the injected provider reported it. <see cref="Handle"/>
/// is the diagnostics instance handle -- stable for the life of the element and how a later call
/// addresses it; <see cref="Parent"/> is its parent's handle (0 at the root) and <see cref="ChildIndex"/>
/// its position among its siblings, so the flat list rebuilds into a tree. <see cref="Name"/> is the
/// element's <c>x:Name</c> when it has one.
/// </summary>
public sealed record LiveXamlNode
{
	public required ulong Handle { get; init; }

	public required ulong Parent { get; init; }

	public required int ChildIndex { get; init; }

	public required string TypeName { get; init; }

	public string? Name { get; init; }
}
