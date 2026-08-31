namespace RoseMcp.Worker.Xaml;

/// <summary>An element the markup gave a name to, which the generated partial turns into a field.</summary>
public sealed record XamlNamedElement
{
	public required string Name { get; init; }

	public required XamlTypeName Type { get; init; }
}
