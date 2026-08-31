namespace RoseMcp.Worker.Xaml;

/// <summary>An element the markup gave a name to, which the generated partial turns into a field.</summary>
public sealed record XamlNamedElement
{
	public required string Name { get; init; }

	public required XamlTypeName Type { get; init; }

	/// <summary>
	/// From x:FieldModifier, defaulting to private as the frameworks do. Markup sets it when another
	/// class needs the field, and ignoring it makes that access inaccessible rather than missing.
	/// </summary>
	public string Modifier { get; init; } = "private";
}
