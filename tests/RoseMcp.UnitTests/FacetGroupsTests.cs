namespace RoseMcp.UnitTests;

/// <summary>
/// The one way an overflowing list describes itself: a group per value, most first, ties by value so
/// the same answer reads the same twice, and nothing grouped under a value an item does not have.
/// </summary>
public sealed class FacetGroupsTests
{
	[Test]
	public void Groups_by_value_most_first_with_ties_in_order_and_leaves_out_items_with_none()
	{
		string?[] values = ["b", "a", null, "b", "c", "a", "b"];

		var groups = FacetGroups.Of(values, value => value, (value, inIt) => (value, inIt.Count));

		groups.ShouldBe([("b", 3), ("a", 2), ("c", 1)]);
	}

	[Test]
	public void Caps_a_facet_with_no_natural_bound()
	{
		IReadOnlyList<int> groups = [1, 2, 3, 4];

		FacetGroups.Capped(groups, 2).ShouldBe([1, 2]);
		FacetGroups.Capped(groups, 10).ShouldBe(groups);
	}
}
