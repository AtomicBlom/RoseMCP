namespace RoseMcp.Contracts;

/// <summary>
/// One edit an apply computed and what became of it. <see cref="Status"/> is <c>applied</c> when it
/// took, or the reason it did not: <c>target not found</c>, <c>property not found</c>, or an apply
/// failure code from the framework. Property changes, additions, removals, attached properties and
/// keyed resources all apply, on named elements and unnamed ones alike.
/// </summary>
public sealed record LiveXamlEditResult
{
	/// <summary>SetProperty, ClearProperty, AddChild, or RemoveChild.</summary>
	public required string Kind { get; init; }

	/// <summary>The element (or parent) the edit targets, as the diff addressed it.</summary>
	public required string Target { get; init; }

	public string? Property { get; init; }

	public string? Value { get; init; }

	public required string Status { get; init; }

	/// <summary>
	/// The handle of the element the edit landed on, as the visual tree lists it: the element a property
	/// changed on, the parent an element was added under, the element removed. Null where the target did
	/// not resolve to anything in the app.
	/// </summary>
	public ulong? Handle { get; init; }

	/// <summary>
	/// What the property said before the edit, rendered the way <c>rose_xaml_properties</c> renders a
	/// value: a colour as <c>#AARRGGBB</c>, a thickness as four numbers. Read by the provider in the same
	/// walk that finds the property to set, so it is the live value an instant before, not what the
	/// markup said. Null for an edit that changes no property, and for one whose property was not found.
	/// </summary>
	public string? OldValue { get; init; }
}
