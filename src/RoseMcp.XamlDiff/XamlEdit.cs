namespace RoseMcp.XamlDiff;

/// <summary>What a single <see cref="XamlEdit"/> does to the live tree.</summary>
public enum XamlEditKind
{
	/// <summary>Set a property on the target element to a value.</summary>
	SetProperty,

	/// <summary>Clear a property on the target element back to its default.</summary>
	ClearProperty,

	/// <summary>Add a child element under the target parent at an index.</summary>
	AddChild,

	/// <summary>Remove the target child element.</summary>
	RemoveChild,
}

/// <summary>
/// One minimal edit between two XAML versions. Elements are addressed by a stable path
/// (<c>#name</c> for a named element, else <c>Type[index]</c> among same-type siblings) built from the
/// root, so both named and unnamed elements can be targeted. A property edit carries the property, the
/// inferred value type, and the value; a structural edit carries the child's markup and index.
/// </summary>
public sealed record XamlEdit
{
	public required XamlEditKind Kind { get; init; }

	/// <summary>The element (for a property edit) or parent (for a structural edit) being changed.</summary>
	public required string Target { get; init; }

	/// <summary>The property, for <see cref="XamlEditKind.SetProperty"/> / <see cref="XamlEditKind.ClearProperty"/>.</summary>
	public string? Property { get; init; }

	/// <summary>The value's inferred WinRT type name, for <see cref="XamlEditKind.SetProperty"/>; empty if unknown.</summary>
	public string? ValueType { get; init; }

	/// <summary>The value, for <see cref="XamlEditKind.SetProperty"/>.</summary>
	public string? Value { get; init; }

	/// <summary>The child's markup, for <see cref="XamlEditKind.AddChild"/>.</summary>
	public string? Payload { get; init; }

	/// <summary>The child's index among its siblings, for <see cref="XamlEditKind.AddChild"/>.</summary>
	public int? Index { get; init; }
}
