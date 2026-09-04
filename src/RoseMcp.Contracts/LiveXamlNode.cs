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

	/// <summary>
	/// The XAML file the element was declared in, when the app carries source info. Null means the
	/// information is absent, which is not the same as the element having no source -- a control
	/// template's parts and an app compiled without diagnostics source info both look like this.
	/// </summary>
	public string? File { get; init; }

	/// <summary>The line in <see cref="File"/>, when there is one.</summary>
	public int? Line { get; init; }

	/// <summary>
	/// How to name this element to a tool that changes one -- <c>#name</c> where it has an
	/// <c>x:Name</c>, else a path of <c>Type[index]</c> segments anchored at its nearest named
	/// ancestor. Called an address rather than a path because it is nothing to do with
	/// <see cref="File"/>.
	/// <para>
	/// It is here because a handle cannot be spoken and an <c>x:Name</c> is often absent: most of
	/// what a click lands on inside a control template was never named in markup, and before this
	/// there was no way to say which element was meant. Computed from the same tree that resolves
	/// it, so it is exact -- unlike the address a XAML diff derives from markup, whose element
	/// order is not always the visual tree's.
	/// </para>
	/// </summary>
	public string? Address { get; init; }
}
