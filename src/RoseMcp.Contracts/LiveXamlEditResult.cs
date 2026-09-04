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
}
