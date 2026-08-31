namespace RoseMcp.Worker.Xaml;

/// <summary>An element the markup gave a name to, which the generated partial turns into a field.</summary>
public sealed record XamlNamedElement
{
	public required string Name { get; init; }

	public required XamlTypeName Type { get; init; }

	/// <summary>
	/// True for the root element, whose field the dialects type differently from every other one.
	/// </summary>
	public bool IsRoot { get; init; }

	/// <summary>
	/// From x:FieldModifier, or null when the markup does not say and the dialect's default applies.
	/// Markup sets it when another class needs the field, and ignoring it makes that access
	/// inaccessible rather than missing.
	/// </summary>
	public string? Modifier { get; init; }
}
