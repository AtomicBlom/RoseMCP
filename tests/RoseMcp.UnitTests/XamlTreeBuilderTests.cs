using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// Turning the provider's flat element list into the tree it describes.
/// <para>
/// Most of what is asserted here is a snapshot that does not quite describe a tree: a handle listed
/// twice, a parent nobody listed, a parent that is its own descendant. Each has the same wrong
/// answer available -- drop the element -- which hands back a tree that reads as complete and is
/// missing a subtree, and nothing on screen would say so.
/// </para>
/// </summary>
public sealed class XamlTreeBuilderTests
{
	/// <summary>
	/// The list is the framework's enumeration order rather than a walk, so a child can arrive
	/// before its parent and every parent has to be resolved after every row exists.
	/// </summary>
	[Test]
	public void A_child_listed_before_its_parent_still_lands_under_it()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(Tree([
			Node(3, parent: 2, "TextBlock"),
			Node(2, parent: 1, "Border"),
			Node(1, parent: 0, "Grid"),
		]));

		Assert.Single(inspection.Roots);
		Assert.Equal("Grid", inspection.Roots[0].TypeName);
		Assert.Equal("Border", inspection.Roots[0].Children[0].TypeName);
		Assert.Equal("TextBlock", inspection.Roots[0].Children[0].Children[0].TypeName);
	}

	/// <summary>
	/// Siblings are in the framework's own child order, not the order they were enumerated in. An
	/// address is a position among siblings, so a list in the wrong order is a set of wrong
	/// addresses.
	/// </summary>
	[Test]
	public void Siblings_are_in_the_frameworks_child_order()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(Tree([
			Node(1, 0, "Grid"),
			Node(4, 1, "Third", childIndex: 2),
			Node(2, 1, "First", childIndex: 0),
			Node(3, 1, "Second", childIndex: 1),
		]));

		Assert.Equal(
			new[] { "First", "Second", "Third" },
			inspection.Roots[0].Children.Select(row => row.TypeName).ToArray());
	}

	/// <summary>
	/// An element naming a parent nobody listed goes to the top and says so. Dropping it would turn
	/// a snapshot that disagrees with itself into a tree quietly missing a subtree.
	/// </summary>
	[Test]
	public void An_element_whose_parent_is_missing_goes_to_the_top_and_is_flagged()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(Tree([
			Node(1, 0, "Grid"),
			Node(9, parent: 77, "Popup"),
		]));

		Assert.Equal(2, inspection.Roots.Count);

		var orphan = inspection.Roots.Single(row => row.TypeName == "Popup");
		Assert.True(orphan.IsOrphan);
		Assert.False(inspection.Roots.Single(row => row.TypeName == "Grid").IsOrphan);
	}

	/// <summary>
	/// A parent that is its own descendant cannot be drawn, and following it is how a tree build
	/// hangs rather than reporting anything. Both go to the top.
	/// </summary>
	[Test]
	public void A_parent_that_is_its_own_descendant_is_not_followed()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(Tree([
			Node(1, parent: 2, "Grid"),
			Node(2, parent: 1, "Border"),
		]));

		Assert.Equal(2, inspection.Roots.Count);
		Assert.True(inspection.Roots.All(row => row.IsOrphan));
	}

	/// <summary>One row per handle: an element cannot be in two places, and two rows would be.</summary>
	[Test]
	public void A_handle_listed_twice_becomes_one_row()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(Tree([
			Node(1, 0, "Grid"),
			Node(2, 1, "Border"),
			Node(2, 1, "Border"),
		]));

		Assert.Single(inspection.Roots[0].Children);
		Assert.Equal(2, inspection.Count);
	}

	/// <summary>
	/// A refresh keeps the rows it still has, which is what keeps a reader's expansion and selection
	/// through the re-read that follows every live edit.
	/// </summary>
	[Test]
	public void A_refresh_keeps_the_rows_it_still_has()
	{
		var inspection = new XamlInspection();
		inspection.Absorb(Tree([Node(1, 0, "Grid"), Node(2, 1, "Border")]));

		var border = inspection.Row(2)!;
		border.IsExpanded = false;
		inspection.Select(border);

		inspection.Absorb(Tree([Node(1, 0, "Grid"), Node(2, 1, "Border"), Node(3, 2, "TextBlock")]));

		Assert.Same(border, inspection.Row(2));
		Assert.False(border.IsExpanded);
		Assert.True(border.IsSelected);
		Assert.Same(border, inspection.Selected);
	}

	/// <summary>
	/// A subtree that has gone takes its rows with it, and the selection with them. Leaving the
	/// properties of an element that is not there any more up is the failure this pane exists to
	/// avoid.
	/// </summary>
	[Test]
	public void A_subtree_that_has_gone_takes_the_selection_with_it()
	{
		var inspection = new XamlInspection();
		inspection.Absorb(Tree([Node(1, 0, "Grid"), Node(2, 1, "Border"), Node(3, 2, "TextBlock")]));

		inspection.Select(inspection.Row(3));

		inspection.Absorb(Tree([Node(1, 0, "Grid")]));

		Assert.Null(inspection.Row(3));
		Assert.Null(inspection.Selected);
		Assert.Empty(inspection.Roots[0].Children);
		Assert.True(inspection.HasPropertiesDetail);
	}

	/// <summary>
	/// An element that moves is the same row under a new parent, so a reader watching it does not
	/// lose it to a live edit that reparented it.
	/// </summary>
	[Test]
	public void An_element_that_moves_keeps_its_row()
	{
		var inspection = new XamlInspection();
		inspection.Absorb(Tree([Node(1, 0, "Grid"), Node(2, 1, "Border"), Node(3, 1, "StackPanel")]));

		var moved = inspection.Row(3)!;

		inspection.Absorb(Tree([Node(1, 0, "Grid"), Node(2, 1, "Border"), Node(3, 2, "StackPanel")]));

		Assert.Same(moved, inspection.Row(3));
		Assert.Single(inspection.Roots[0].Children);
		Assert.Same(moved, inspection.Row(2)!.Children[0]);
		Assert.Same(inspection.Row(2), moved.Parent);
	}

	/// <summary>
	/// Newly seen elements open to the depth somebody came to look at, and no further: unfolding a
	/// control template's internals is most of the elements in any real app.
	/// </summary>
	[Test]
	public void What_arrives_opens_to_the_depth_asked_for()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(Tree([
			Node(1, 0, "Window"),
			Node(2, 1, "Grid"),
			Node(3, 2, "Border"),
			Node(4, 3, "TextBlock"),
		]));

		Assert.True(inspection.Row(1)!.IsExpanded);
		Assert.True(inspection.Row(2)!.IsExpanded);
		Assert.True(inspection.Row(3)!.IsExpanded);
		Assert.False(inspection.Row(4)!.IsExpanded);
	}

	/// <summary>The chain a reveal expands, root first and the element itself last.</summary>
	[Test]
	public void The_ancestors_of_an_element_run_from_the_root_down_to_it()
	{
		var inspection = new XamlInspection();
		inspection.Absorb(Tree([Node(1, 0, "Grid"), Node(2, 1, "Border"), Node(3, 2, "TextBlock")]));

		Assert.Equal(
			new[] { "Grid", "Border", "TextBlock" },
			XamlTreeBuilder.Ancestors(inspection, 3).Select(row => row.TypeName).ToArray());

		Assert.Empty(XamlTreeBuilder.Ancestors(inspection, 99));
	}

	private static LiveXamlTree Tree(LiveXamlNode[] nodes) => new() { Nodes = nodes, Total = nodes.Length };

	private static LiveXamlNode Node(
		ulong handle,
		ulong parent,
		string typeName,
		int childIndex = 0,
		string? name = null) =>
		new()
		{
			Handle = handle,
			Parent = parent,
			ChildIndex = childIndex,
			TypeName = typeName,
			Name = name,
		};
}
