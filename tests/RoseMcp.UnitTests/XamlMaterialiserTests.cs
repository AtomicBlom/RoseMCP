using RoseMcp.XamlDiff;

namespace RoseMcp.UnitTests;

/// <summary>
/// Taking added markup apart into the steps a live tree can be built from. Pure -- markup in, steps
/// out -- which is the point of it living here: the host that consumes these cannot be unit tested,
/// because it targets Windows and the test projects cannot see inside it.
/// </summary>
public sealed class XamlMaterialiserTests
{
	[Fact]
	public void Creates_an_element_sets_its_properties_then_adds_it()
	{
		var steps = XamlMaterialiser.Steps("""<Border Background="#FFFF0000" />""", "#Pair", 2);

		Assert.Collection(
			steps,
			step =>
			{
				Assert.Equal(XamlStepKind.Create, step.Kind);
				Assert.Equal("$0", step.Target);
				Assert.Equal("Border", step.TypeName);
			},
			step =>
			{
				Assert.Equal(XamlStepKind.SetProperty, step.Kind);
				Assert.Equal("$0", step.Target);
				Assert.Equal("Background", step.Property);
				Assert.Equal("#FFFF0000", step.Value);
				Assert.Equal("Windows.UI.Xaml.Media.SolidColorBrush", step.ValueType);
			},
			step =>
			{
				Assert.Equal(XamlStepKind.AddChild, step.Kind);
				Assert.Equal("#Pair", step.Target);
				Assert.Equal("$0", step.Child);
				Assert.Equal(2, step.Index);
			});
	}

	/// <summary>
	/// The ordering guarantee, and the reason it is worth a test of its own: the subtree is finished
	/// before anything attaches it to the running app, so the framework never lays out or renders a
	/// half-built element. The attach is the last step, always.
	/// </summary>
	[Fact]
	public void Attaches_the_finished_subtree_to_the_app_last()
	{
		var steps = XamlMaterialiser.Steps(
			"""<Border Padding="6"><TextBlock Text="hi" /></Border>""",
			"#Pair",
			0).ToList();

		var attach = steps[^1];
		Assert.Equal(XamlStepKind.AddChild, attach.Kind);
		Assert.Equal("#Pair", attach.Target);
		Assert.Equal("$0", attach.Child);

		// Nothing before it touches the app: every other step names a slot.
		Assert.All(steps[..^1], step => Assert.StartsWith("$", step.Target, StringComparison.Ordinal));

		// And the child is in its parent before the parent goes anywhere.
		var nested = steps.Single(step => step.Kind == XamlStepKind.AddChild && step.Target == "$0");
		Assert.Equal("$1", nested.Child);
		Assert.True(steps.IndexOf(nested) < steps.Count - 1, "the nested add must come before the attach");
	}

	[Fact]
	public void Gives_every_element_its_own_slot()
	{
		var steps = XamlMaterialiser.Steps(
			"""<StackPanel><Border /><Border /></StackPanel>""",
			"#Pair",
			0);

		var created = steps.Where(step => step.Kind == XamlStepKind.Create).Select(step => step.Target).ToList();
		Assert.Equal(["$0", "$1", "$2"], created);

		// The two Borders go in at 0 and 1 under the panel, not both at 0.
		var adds = steps.Where(step => step.Kind == XamlStepKind.AddChild && step.Target == "$0").ToList();
		Assert.Equal([0, 1], adds.Select(step => step.Index));
	}

	/// <summary>
	/// Property-element syntax is a property, not a child. Adding <c>Grid.RowDefinitions</c> as an
	/// element would fail somewhere that reads like a fault in the element beside it.
	/// </summary>
	[Fact]
	public void Passes_over_property_element_syntax_rather_than_adding_it_as_a_child()
	{
		var steps = XamlMaterialiser.Steps(
			"""<Grid><Grid.RowDefinitions><RowDefinition /></Grid.RowDefinitions><Border /></Grid>""",
			"#Pair",
			0);

		Assert.DoesNotContain(steps, step => step.TypeName is "Grid.RowDefinitions" or "RowDefinition");
		Assert.Equal(["Grid", "Border"], steps.Where(step => step.Kind == XamlStepKind.Create).Select(step => step.TypeName));
	}

	/// <summary>
	/// A live add cannot carry a name, so the diff says so. Names come from a namescope the markup
	/// compiler built, and nothing settable at runtime puts an element into one.
	/// </summary>
	[Fact]
	public void Notes_that_an_added_element_cannot_keep_its_name()
	{
		Assert.True(XamlMaterialiser.NamesAnything("""<Border xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="new" />"""));
		Assert.False(XamlMaterialiser.NamesAnything("""<Border Padding="6" />"""));
	}
}
