using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// One property of a live XAML element, and where its value came from.
/// <para>
/// Provenance is the whole reason the list is worth reading: the same value means different things
/// set on the element, inherited from a parent, or handed over by a style nobody looking at the
/// markup would think to open. Getting a bucket wrong is a confident wrong answer about which file
/// to go and edit.
/// </para>
/// </summary>
public sealed class PropertyRowTests
{
	[Test]
	[Arguments("Local", PropertyOrigin.Local)]
	[Arguments("local", PropertyOrigin.Local)]
	[Arguments("Inherited", PropertyOrigin.Inherited)]
	[Arguments("Animation", PropertyOrigin.Animation)]
	[Arguments("Default", PropertyOrigin.Default)]
	[Arguments("Unknown", PropertyOrigin.Default)]
	[Arguments("Style", PropertyOrigin.Style)]
	[Arguments("StyleTrigger", PropertyOrigin.Style)]
	[Arguments("ImplicitStyleReference", PropertyOrigin.Style)]
	[Arguments("ParentTemplate", PropertyOrigin.Style)]
	public void A_provenance_falls_in_the_bucket_a_reader_acts_on(string provenance, PropertyOrigin origin) =>
		Assert.Equal(origin, PropertyRow.OriginOf(provenance));

	/// <summary>
	/// <c>DefaultStyle</c> is a style, and a substring match would file it under Default -- which
	/// says "the framework picked this" about a value a theme chose, and sends a reader looking in
	/// the wrong place.
	/// </summary>
	[Test]
	public void A_default_style_is_a_style()
	{
		Assert.Equal(PropertyOrigin.Style, PropertyRow.OriginOf("DefaultStyle"));
		Assert.Equal(PropertyOrigin.Style, PropertyRow.OriginOf("DefaultStyleTrigger"));
	}

	/// <summary>
	/// A provenance this does not know is shown as itself rather than filed under Default. The
	/// framework's list is longer than the buckets, and guessing at one nobody has seen would be the
	/// same confident wrong answer in a case nobody was watching.
	/// </summary>
	[Test]
	public void A_provenance_nobody_knows_is_kept_as_it_is()
	{
		var row = new PropertyRow(Property("Fill", provenance: "SomethingNew"));

		Assert.Equal(PropertyOrigin.Elsewhere, row.Origin);
		Assert.Equal("SomethingNew", row.Provenance);
		Assert.True(row.IsElsewhere);
		Assert.False(row.IsDefault);
	}

	/// <summary>
	/// A value that exists and could not be rendered reads differently from one that is null. Both
	/// showed as nothing before, which is how a CornerRadius rendering as empty looked exactly like
	/// a property nobody had set.
	/// </summary>
	[Test]
	public void An_unrenderable_value_and_a_null_one_do_not_read_alike()
	{
		Assert.Equal(PropertyRow.Unavailable, new PropertyRow(Property("CornerRadius", unavailable: true)).Value);
		Assert.Equal(PropertyRow.Null, new PropertyRow(Property("Tag", value: null)).Value);
		Assert.Equal(string.Empty, new PropertyRow(Property("Text", value: "")).Value);
	}

	/// <summary>
	/// What the markup set first, then what a style set, then the rest, and the framework's defaults
	/// last. Sorted by name alone, the two properties somebody actually set are buried among ninety
	/// defaults -- which is the state `includeDefaults` is turned on to escape and then regretted.
	/// </summary>
	[Test]
	public void What_was_set_comes_before_what_the_framework_chose()
	{
		var sorted = PropertyRow.Sort([
			Property("Zebra", provenance: "Default"),
			Property("Alpha", provenance: "Inherited"),
			Property("Beta", provenance: "Local"),
			Property("Gamma", provenance: "Style"),
			Property("Alpha2", provenance: "Animation"),
			Property("Delta", provenance: "Local"),
		]);

		Assert.Equal(
			new[] { "Beta", "Delta", "Gamma", "Alpha", "Alpha2", "Zebra" },
			sorted.Select(row => row.Name).ToArray());
	}

	/// <summary>
	/// The file a property carries is where the style or template that set it lives, and it is null
	/// whenever the element itself set the value -- so a row only offers one when there is somewhere
	/// else to go and look.
	/// </summary>
	[Test]
	public void A_row_points_at_the_style_that_set_it_when_there_is_one()
	{
		var fromStyle = new PropertyRow(Property(
			"Background",
			provenance: "Style",
			sourceFile: @"D:\app\Themes\Generic.xaml",
			sourceLine: 88));

		Assert.Equal("Generic.xaml:88", fromStyle.Where);
		Assert.True(fromStyle.HasWhere);

		var fromMarkup = new PropertyRow(Property("Background", provenance: "Local"));
		Assert.Equal(string.Empty, fromMarkup.Where);
		Assert.False(fromMarkup.HasWhere);
	}

	private static LiveXamlProperty Property(
		string name,
		string? value = "#FF445566",
		string provenance = "Local",
		bool unavailable = false,
		string? sourceFile = null,
		int? sourceLine = null) =>
		new()
		{
			Name = name,
			Value = value,
			ValueUnavailable = unavailable,
			ValueType = "Brush",
			Provenance = provenance,
			SourceFile = sourceFile,
			SourceLine = sourceLine,
		};
}
