namespace RoseMcp.Contracts;

/// <summary>
/// One property of a live XAML element and how it got its value. <see cref="Provenance"/> is where the
/// effective value came from -- <c>Local</c> (set on the element), <c>Style</c>, <c>Inherited</c>,
/// <c>Animation</c>, <c>Default</c>, and so on -- which is what tells a set value apart from a framework
/// default. <see cref="SourceFile"/> and <see cref="SourceLine"/> point at the XAML that set it, when
/// the app carries source info; they are null otherwise.
/// </summary>
public sealed record LiveXamlProperty
{
	public required string Name { get; init; }

	/// <summary>The rendered value; null when the value is null.</summary>
	public string? Value { get; init; }

	public string? ValueType { get; init; }

	public string? DeclaringType { get; init; }

	/// <summary>Where the value came from: Local, Style, Inherited, Animation, Default, and so on.</summary>
	public required string Provenance { get; init; }

	public string? SourceFile { get; init; }

	public int? SourceLine { get; init; }

	public int? SourceColumn { get; init; }
}
