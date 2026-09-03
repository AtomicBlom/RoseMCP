namespace RoseMcp.Contracts;

/// <summary>
/// One edit a hot reload computed and what became of it. <see cref="Status"/> is <c>applied</c> when it
/// took, or the reason it did not -- <c>target not found</c>, <c>property not found</c>, an apply
/// failure code, or <c>unsupported: ...</c> for the edits the live applier does not do yet (structural
/// changes and unnamed-element addressing).
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
