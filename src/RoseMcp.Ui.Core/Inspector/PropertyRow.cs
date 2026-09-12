using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Where a live element's property value came from. It is the whole reason a property list is worth
/// reading: the same value means different things set on the element, inherited from a parent, or
/// handed over by a style nobody looking at the markup would think to open.
/// <para>
/// Five buckets rather than the framework's dozen. <c>StyleTrigger</c>, <c>ImplicitStyleReference</c>
/// and <c>ParentTemplate</c> are all "a style or template did this", which is the distinction a
/// reader acts on; keeping them apart would colour a list five ways for a difference nobody uses.
/// <see cref="Elsewhere"/> is the escape hatch, so a provenance this does not know is shown as
/// itself rather than quietly filed under Default.
/// </para>
/// </summary>
public enum PropertyOrigin
{
	Local,
	Style,
	Inherited,
	Animation,
	Elsewhere,
	Default,
}

/// <summary>
/// One property of a live XAML element, with where its value came from.
/// </summary>
public sealed class PropertyRow
{
	/// <summary>What a value that exists and could not be rendered is shown as.</summary>
	public const string Unavailable = "(unavailable)";

	/// <summary>What a null value is shown as, which is not the same as an unrenderable one.</summary>
	public const string Null = "(null)";

	public PropertyRow(LiveXamlProperty property)
	{
		Name = property.Name;
		Origin = OriginOf(property.Provenance);
		Provenance = property.Provenance;

		Value = property.ValueUnavailable ? Unavailable : property.Value ?? Null;
		TypeName = property.ValueType ?? string.Empty;
		HasTypeName = TypeName.Length > 0;

		// Where the style or template that set it is declared. Null whenever the element set it
		// itself, in which case the element's own declaration is the only location there is and the
		// header already carries it.
		Where = Format.FileLine(property.SourceFile, property.SourceLine);
		HasWhere = Where.Length > 0;

		IsLocal = Origin == PropertyOrigin.Local;
		IsStyle = Origin == PropertyOrigin.Style;
		IsInherited = Origin == PropertyOrigin.Inherited;
		IsAnimation = Origin == PropertyOrigin.Animation;
		IsDefault = Origin == PropertyOrigin.Default;
		IsElsewhere = Origin == PropertyOrigin.Elsewhere;
	}

	public string Name { get; }

	/// <summary>The rendered value, or a parenthesised word for the two ways there is not one.</summary>
	public string Value { get; }

	/// <summary>The value's type, when the provider named one.</summary>
	public string TypeName { get; }

	public bool HasTypeName { get; }

	/// <summary>What the framework called the provenance, verbatim, which is what is shown.</summary>
	public string Provenance { get; }

	public PropertyOrigin Origin { get; }

	public string Where { get; }

	public bool HasWhere { get; }

	public bool IsLocal { get; }

	public bool IsStyle { get; }

	public bool IsInherited { get; }

	public bool IsAnimation { get; }

	public bool IsDefault { get; }

	public bool IsElsewhere { get; }

	/// <summary>
	/// The order properties are shown in: what the markup set, then what a style set, then what was
	/// inherited, then animations, then the rest, then the framework's defaults -- and alphabetical
	/// inside each.
	/// <para>
	/// By provenance rather than by name because that is the order somebody reads in. A list sorted
	/// only by name buries the two properties the markup actually set among ninety defaults, which
	/// is the state <c>includeDefaults</c> is usually turned on to escape and then regretted.
	/// </para>
	/// </summary>
	public static IReadOnlyList<PropertyRow> Sort(IEnumerable<LiveXamlProperty> properties) =>
	[
		.. properties
			.Select(property => new PropertyRow(property))
			.OrderBy(row => (int)row.Origin)
			.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
	];

	/// <summary>
	/// Which bucket a framework provenance falls in. Matched whole and case-insensitively: the
	/// strings come from the framework's own enum, and a substring match would put
	/// <c>DefaultStyle</c>, which is a style, under Default.
	/// </summary>
	public static PropertyOrigin OriginOf(string provenance) => provenance.ToLowerInvariant() switch
	{
		"local" => PropertyOrigin.Local,
		"inherited" => PropertyOrigin.Inherited,
		"animation" => PropertyOrigin.Animation,
		"default" or "unknown" => PropertyOrigin.Default,
		"style"
			or "defaultstyle"
			or "defaultstyletrigger"
			or "styletrigger"
			or "implicitstylereference"
			or "templatetrigger"
			or "parenttemplate"
			or "parenttemplatetrigger" => PropertyOrigin.Style,
		_ => PropertyOrigin.Elsewhere,
	};
}
