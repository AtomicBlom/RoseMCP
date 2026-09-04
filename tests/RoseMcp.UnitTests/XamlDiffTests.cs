using RoseMcp.XamlDiff;

namespace RoseMcp.UnitTests;

/// <summary>
/// The XAML diff engine is pure -- two markup strings in, a minimal edit list out -- so it is a unit
/// test. These lock in what the live-apply side will consume: property changes on named and unnamed
/// elements, attached properties, clears, and structural add/remove.
/// </summary>
public sealed class XamlDiffTests
{
	private const string Ns =
		"xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
			+ "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

	[Fact]
	public void A_changed_property_on_a_named_element_is_one_set_addressed_by_name()
	{
		var edits = Compute(
			$"<Grid {Ns}><Border x:Name=\"pane\" Background=\"#FF000000\" /></Grid>",
			$"<Grid {Ns}><Border x:Name=\"pane\" Background=\"#FF0000FF\" /></Grid>");

		var edit = Assert.Single(edits);
		Assert.Equal(XamlEditKind.SetProperty, edit.Kind);
		Assert.Equal("#pane", edit.Target);
		Assert.Equal("Background", edit.Property);
		Assert.Equal("#FF0000FF", edit.Value);
		Assert.Equal("Windows.UI.Xaml.Media.SolidColorBrush", edit.ValueType);
	}

	[Fact]
	public void An_unchanged_tree_produces_no_edits()
	{
		var xaml = $"<Grid {Ns}><Border x:Name=\"pane\" Background=\"#FF000000\" Opacity=\"1\" /></Grid>";
		Assert.Empty(Compute(xaml, xaml));
	}

	[Fact]
	public void A_changed_property_on_an_unnamed_element_is_addressed_by_path()
	{
		var edits = Compute(
			$"<Grid {Ns}><Border Width=\"10\" /></Grid>",
			$"<Grid {Ns}><Border Width=\"20\" /></Grid>");

		var edit = Assert.Single(edits);
		Assert.Equal(XamlEditKind.SetProperty, edit.Kind);
		Assert.Equal("Grid[0]/Border[0]", edit.Target);
		Assert.Equal("Width", edit.Property);
		Assert.Equal("20", edit.Value);
		Assert.Equal("Windows.Foundation.Double", edit.ValueType);
	}

	[Fact]
	public void Two_same_named_types_from_different_namespaces_do_not_share_an_address()
	{
		// The sibling index was counted over the *qualified* name and then printed with the *local*
		// one, so a namespace-qualified element and a framework element of the same local name each
		// counted only their own kind and both came out at index 0. Two different elements with one
		// address is the worst shape this can fail in: an apply lands on whichever the resolver
		// reaches first, and the reader sees a plausible target and a successful edit.
		var edits = Compute(
			$"<Grid {Ns} xmlns:local=\"using:App\"><local:Border Opacity=\"1\" /><Border Opacity=\"1\" /></Grid>",
			$"<Grid {Ns} xmlns:local=\"using:App\"><local:Border Opacity=\"0.5\" /><Border Opacity=\"0.25\" /></Grid>");

		Assert.Equal(2, edits.Count);
		Assert.Equal(2, edits.Select(edit => edit.Target).Distinct().Count());
	}

	[Fact]
	public void An_unnamed_element_under_a_named_ancestor_is_anchored_at_the_name()
	{
		var edits = Compute(
			$"<Grid {Ns}><StackPanel x:Name=\"panel\"><Border Opacity=\"1\" /></StackPanel></Grid>",
			$"<Grid {Ns}><StackPanel x:Name=\"panel\"><Border Opacity=\"0.5\" /></StackPanel></Grid>");

		var edit = Assert.Single(edits);
		Assert.Equal("#panel/Border[0]", edit.Target);
	}

	[Fact]
	public void A_removed_attribute_is_a_clear()
	{
		var edits = Compute(
			$"<Grid {Ns}><Border x:Name=\"pane\" Background=\"#FF000000\" /></Grid>",
			$"<Grid {Ns}><Border x:Name=\"pane\" /></Grid>");

		var edit = Assert.Single(edits);
		Assert.Equal(XamlEditKind.ClearProperty, edit.Kind);
		Assert.Equal("#pane", edit.Target);
		Assert.Equal("Background", edit.Property);
	}

	[Fact]
	public void An_attached_property_change_is_a_set_keeping_its_dotted_name()
	{
		var edits = Compute(
			$"<Grid {Ns}><Border x:Name=\"pane\" Grid.Row=\"0\" /></Grid>",
			$"<Grid {Ns}><Border x:Name=\"pane\" Grid.Row=\"2\" /></Grid>");

		var edit = Assert.Single(edits);
		Assert.Equal(XamlEditKind.SetProperty, edit.Kind);
		Assert.Equal("Grid.Row", edit.Property);
		Assert.Equal("2", edit.Value);
	}

	[Fact]
	public void An_added_child_is_a_structural_edit_carrying_its_markup()
	{
		var edits = Compute(
			$"<StackPanel {Ns}><Border x:Name=\"a\" /></StackPanel>",
			$"<StackPanel {Ns}><Border x:Name=\"a\" /><Button x:Name=\"b\" Content=\"Go\" /></StackPanel>");

		var edit = Assert.Single(edits);
		Assert.Equal(XamlEditKind.AddChild, edit.Kind);
		Assert.Equal(1, edit.Index);
		Assert.Contains("Button", edit.Payload);
		Assert.Contains("Go", edit.Payload);
	}

	[Fact]
	public void A_removed_child_is_a_structural_edit()
	{
		var edits = Compute(
			$"<StackPanel {Ns}><Border x:Name=\"a\" /><Button x:Name=\"b\" /></StackPanel>",
			$"<StackPanel {Ns}><Border x:Name=\"a\" /></StackPanel>");

		var edit = Assert.Single(edits);
		Assert.Equal(XamlEditKind.RemoveChild, edit.Kind);
		Assert.Equal("#b", edit.Target);
	}


	/// <summary>
	/// A single-number struct is the case the name-and-shape inference gets wrong on its own:
	/// CornerRadius="0" parses as a number, so it went out as a Double, which the apply side's
	/// CreateInstance built quite happily and SetProperty then rejected with a bare E_FAIL. The
	/// provider recovers by asking the property its own declared type, but the hint should be right
	/// in the first place -- and every property whose type cannot be read off its value has to be
	/// named here, because inference cannot get there.
	/// </summary>
	[Fact]
	public void A_single_number_corner_radius_is_typed_as_a_corner_radius_not_a_double()
	{
		var edits = Compute(
			$"<Border {Ns} x:Name=\"pane\" CornerRadius=\"8\" />",
			$"<Border {Ns} x:Name=\"pane\" CornerRadius=\"0\" />");

		var edit = Assert.Single(edits);
		Assert.Equal("CornerRadius", edit.Property);
		Assert.Equal("0", edit.Value);
		Assert.Equal("Windows.UI.Xaml.CornerRadius", edit.ValueType);
	}

	/// <summary>
	/// The corner cases either side of it, so the fix for the above cannot quietly become "call every
	/// number a CornerRadius": a genuine Double property stays a Double, and a four-part CornerRadius
	/// is still a CornerRadius rather than being mistaken for the Thickness it looks exactly like.
	/// </summary>
	[Fact]
	public void A_number_on_a_double_property_is_still_a_double()
	{
		var edits = Compute(
			$"<Border {Ns} x:Name=\"pane\" Opacity=\"1\" />",
			$"<Border {Ns} x:Name=\"pane\" Opacity=\"0.5\" />");

		Assert.Equal("Windows.Foundation.Double", Assert.Single(edits).ValueType);
	}

	[Fact]
	public void A_four_part_corner_radius_is_not_mistaken_for_a_thickness()
	{
		var edits = Compute(
			$"<Border {Ns} x:Name=\"pane\" CornerRadius=\"8,8,0,0\" />",
			$"<Border {Ns} x:Name=\"pane\" CornerRadius=\"0,0,8,8\" />");

		Assert.Equal("Windows.UI.Xaml.CornerRadius", Assert.Single(edits).ValueType);
	}

	private static IReadOnlyList<XamlEdit> Compute(string oldXaml, string newXaml)
		=> XamlDiff.XamlDiff.Compute(oldXaml, newXaml).Edits;
}
