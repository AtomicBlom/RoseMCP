using RoseMcp.XamlDiff;

namespace RoseMcp.UnitTests;

/// <summary>
/// Taking added markup apart into the steps a live tree can be built from. Pure -- markup in, steps
/// out -- which is the point of it living here: the host that consumes these cannot be unit tested,
/// because it targets Windows and the test projects cannot see inside it.
/// </summary>
public sealed class XamlMaterialiserTests
{
	[Test]
	public void Creates_an_element_sets_its_properties_then_adds_it()
	{
		var steps = XamlMaterialiser.Steps("""<Border Background="#FFFF0000" />""", "#Pair", 2);

		steps.Select(step => step.Kind).ShouldBe([XamlStepKind.Create, XamlStepKind.SetProperty, XamlStepKind.AddChild]);

		var (create, set, add) = (steps[0], steps[1], steps[2]);

		create.Target.ShouldBe("$0");
		create.TypeName.ShouldBe("Border");

		set.Target.ShouldBe("$0");
		set.Property.ShouldBe("Background");
		set.Value.ShouldBe("#FFFF0000");
		set.ValueType.ShouldBe("Windows.UI.Xaml.Media.SolidColorBrush");

		add.Target.ShouldBe("#Pair");
		add.Child.ShouldBe("$0");
		add.Index.ShouldBe(2);
	}

	/// <summary>
	/// The ordering guarantee, and the reason it is worth a test of its own: the subtree is finished
	/// before anything attaches it to the running app, so the framework never lays out or renders a
	/// half-built element. The attach is the last step, always.
	/// </summary>
	[Test]
	public void Attaches_the_finished_subtree_to_the_app_last()
	{
		var steps = XamlMaterialiser.Steps(
			"""<Border Padding="6"><TextBlock Text="hi" /></Border>""",
			"#Pair",
			0).ToList();

		var attach = steps[^1];
		attach.Kind.ShouldBe(XamlStepKind.AddChild);
		attach.Target.ShouldBe("#Pair");
		attach.Child.ShouldBe("$0");

		// Nothing before it touches the app: every other step names a slot.
		foreach (var step in steps[..^1])
		{
			step.Target.ShouldStartWith("$", Case.Sensitive);
		}

		// And the child is in its parent before the parent goes anywhere.
		var nested = steps.Single(step => step.Kind == XamlStepKind.AddChild && step.Target == "$0");
		nested.Child.ShouldBe("$1");
		steps.IndexOf(nested).ShouldBeLessThan(steps.Count - 1, "the nested add must come before the attach");
	}

	[Test]
	public void Gives_every_element_its_own_slot()
	{
		var steps = XamlMaterialiser.Steps(
			"""<StackPanel><Border /><Border /></StackPanel>""",
			"#Pair",
			0);

		var created = steps.Where(step => step.Kind == XamlStepKind.Create).Select(step => step.Target).ToList();
		created.ShouldBe(["$0", "$1", "$2"]);

		// The two Borders go in at 0 and 1 under the panel, not both at 0.
		var adds = steps.Where(step => step.Kind == XamlStepKind.AddChild && step.Target == "$0").ToList();
		adds.Select(step => step.Index).ShouldBe([0, 1]);
	}

	/// <summary>
	/// Property-element syntax is a property, not a child. Adding <c>Grid.RowDefinitions</c> as an
	/// element would fail somewhere that reads like a fault in the element beside it.
	/// </summary>
	[Test]
	public void Passes_over_property_element_syntax_rather_than_adding_it_as_a_child()
	{
		var steps = XamlMaterialiser.Steps(
			"""<Grid><Grid.RowDefinitions><RowDefinition /></Grid.RowDefinitions><Border /></Grid>""",
			"#Pair",
			0);

		steps.Any(step => step.TypeName is "Grid.RowDefinitions" or "RowDefinition").ShouldBeFalse();
		steps.Where(step => step.Kind == XamlStepKind.Create).Select(step => step.TypeName).ShouldBe(["Grid", "Border"]);
	}

	/// <summary>
	/// A live add cannot carry a name, so the diff says so. Names come from a namescope the markup
	/// compiler built, and nothing settable at runtime puts an element into one.
	/// </summary>
	[Test]
	public void Notes_that_an_added_element_cannot_keep_its_name()
	{
		XamlMaterialiser.NamesAnything("""<Border xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="new" />""").ShouldBeTrue();
		XamlMaterialiser.NamesAnything("""<Border Padding="6" />""").ShouldBeFalse("markup with no x:Name names nothing");
	}
}
