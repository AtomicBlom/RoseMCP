namespace RoseMcp.Contracts;

/// <summary>
/// One element's properties, read from the live app by injecting the diagnostics provider. By default
/// only set (non-default) properties are returned, each with its provenance; the framework defaults are
/// included on request. <see cref="SourceFile"/>/<see cref="SourceLine"/> locate the element's own XAML
/// declaration when the app carries source info. <see cref="Detail"/> is set, with no properties, when
/// the element could not be read.
/// </summary>
public sealed record LiveXamlProperties
{
	public required ulong Handle { get; init; }

	/// <summary>The element's type, when the provider reported it.</summary>
	public string? TypeName { get; init; }

	public string? SourceFile { get; init; }

	public int? SourceLine { get; init; }

	public int? SourceColumn { get; init; }

	public IReadOnlyList<LiveXamlProperty> Properties { get; init; } = [];

	public int Count => Properties.Count;

	public string? Detail { get; init; }
}
