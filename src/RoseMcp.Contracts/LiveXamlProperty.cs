namespace RoseMcp.Contracts;

/// <summary>
/// One property of a live XAML element and how it got its value. <see cref="Provenance"/> is where the
/// effective value came from -- <c>Local</c> (set on the element), <c>Style</c>, <c>Inherited</c>,
/// <c>Animation</c>, <c>Default</c>, and so on -- which is what tells a set value apart from a framework
/// default.
/// <para>
/// <see cref="SourceFile"/> locates the <em>thing that set the value</em> -- the style or template it
/// came from -- and is null whenever that thing is the element itself. That is not a gap: XAML
/// diagnostics reports source information per source object, not per property, so for a value set on
/// the element the only location available is the element's own tag, which
/// <see cref="LiveXamlProperties.SourceFile"/> already carries. Repeating it per property implied a
/// precision that does not exist, and made a real attribution indistinguishable from a copied one.
/// </para>
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

	/// <summary>Where the style or template that set this value is declared; null when the element set it.</summary>
	public string? SourceFile { get; init; }

	public int? SourceLine { get; init; }

	public int? SourceColumn { get; init; }
}
