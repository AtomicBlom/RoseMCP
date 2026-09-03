namespace RoseMcp.Contracts;

/// <summary>
/// One element in the stack under a click, topmost first, as the framework's own hit test ordered
/// them. The first is what the app's input system would have routed the click to; the rest are what
/// it sits inside.
/// <para>
/// The stack is returned because one element is rarely the one wanted even when it is the right
/// answer: a click on a button lands on some part of its template, and a click meant for a container
/// lands on the content inside it. Walking the stack costs no round trip.
/// </para>
/// </summary>
public sealed record LiveXamlSelectionCandidate
{
	public required ulong Handle { get; init; }

	public required string TypeName { get; init; }

	/// <summary>The element's <c>x:Name</c>, when it has one.</summary>
	public string? Name { get; init; }

	/// <summary>
	/// Whether the type belongs to the XAML framework rather than the app or a library. A coarse
	/// signal for narrowing a stack to the app's own elements, and no more than that: a Button
	/// written in the app's markup is a framework type.
	/// </summary>
	public bool IsFrameworkType { get; init; }

	/// <summary>The XAML file that declared it, when the app carries source info; see LiveXamlNode.</summary>
	public string? File { get; init; }

	public int? Line { get; init; }
}
