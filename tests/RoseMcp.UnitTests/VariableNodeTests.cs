using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// A value in a stopped frame, and what happens when somebody opens it.
/// <para>
/// Expansion is on demand because an object graph has no bound: a widget holding a parent pointer is
/// a cycle, and reading one eagerly is a debugger that hangs on the stop it was asked to describe.
/// So what is asserted here is the bookkeeping around a fetch, which is where that goes wrong.
/// </para>
/// </summary>
public sealed class VariableNodeTests
{
	/// <summary>
	/// An object shows its type rather than its value, because a value would mean running the
	/// debuggee's <c>ToString</c> inside a stop taken to inspect it.
	/// </summary>
	[Test]
	public void An_object_reads_as_its_type_and_a_primitive_as_its_value()
	{
		new VariableNode(Variable("count", value: "42", type: "int", children: false)).Value.ShouldBe("42");
		new VariableNode(Variable("widget", value: null, type: "MyApp.Widget", children: true)).Value.ShouldBe("{MyApp.Widget}");
	}

	/// <summary>The expander appears only where the host has said there is something behind it.</summary>
	[Test]
	public void Only_a_value_with_something_inside_offers_to_expand()
	{
		new VariableNode(Variable("widget", null, "MyApp.Widget", children: true)).HasUnrealizedChildren.ShouldBeTrue();
		new VariableNode(Variable("count", "42", "int", children: false)).HasUnrealizedChildren.ShouldBeFalse();
	}

	[Test]
	public void Expanding_fills_the_children_and_takes_the_expander_away()
	{
		var node = new VariableNode(Variable("widget", null, "MyApp.Widget", children: true));

		node.Loading();
		node.IsLoading.ShouldBeTrue();

		node.Fill(Expansion(Variable("title", "\"hello\"", "string", children: false)));

		node.IsLoading.ShouldBeFalse();
		node.HasUnrealizedChildren.ShouldBeFalse();
		node.Children.ShouldHaveSingleItem().Name.ShouldBe("title");
		node.HasLoadDetail.ShouldBeFalse();
	}

	/// <summary>
	/// An empty answer closes the expander too. A node that went on offering to expand is one a
	/// reader clicks again, and every click is another read of a value the host has already
	/// answered.
	/// </summary>
	[Test]
	public void A_value_that_turns_out_to_hold_nothing_stops_offering_to_expand()
	{
		var node = new VariableNode(Variable("widget", null, "MyApp.Widget", children: true));

		node.Fill(Expansion());

		node.Children.ShouldBeEmpty();
		node.HasUnrealizedChildren.ShouldBeFalse();
	}

	/// <summary>
	/// A list showing a hundred of its four thousand elements looks exactly like a list of a
	/// hundred, so the difference is said rather than left to be noticed.
	/// </summary>
	[Test]
	public void A_truncated_expansion_says_how_much_it_is_showing()
	{
		var node = new VariableNode(Variable("items", null, "System.Int32[]", children: true));

		node.Fill(Expansion(truncated: true, total: 4000, Variable("[0]", "1", "int", children: false)));

		node.HasLoadDetail.ShouldBeTrue();
		node.LoadDetail.ShouldBe("showing 1 of 4000");
	}

	/// <summary>
	/// A failure keeps the expander, because the usual reason is the stop having ended rather than
	/// the value being empty, and the next stop is worth another try.
	/// </summary>
	[Test]
	public void A_failed_expansion_says_why_and_stays_expandable()
	{
		var node = new VariableNode(Variable("widget", null, "MyApp.Widget", children: true));

		node.Loading();
		node.Failed("The target is no longer stopped.");

		node.IsLoading.ShouldBeFalse();
		node.HasUnrealizedChildren.ShouldBeTrue();
		node.LoadDetail.ShouldBe("The target is no longer stopped.");
	}

	private static LiveVariable Variable(string name, string? value, string? type, bool children) => new()
	{
		Name = name,
		Kind = "local",
		TypeName = type,
		Value = value,
		Path = $"local:{name}",
		HasChildren = children,
	};

	private static LiveValueExpansion Expansion(params LiveVariable[] children) =>
		Expansion(truncated: false, total: children.Length, children);

	private static LiveValueExpansion Expansion(bool truncated, int total, params LiveVariable[] children) => new()
	{
		Execution = LiveExecutionState.StoppedAtBreakpoint,
		Path = "local:widget",
		Children = children,
		Total = total,
		Truncated = truncated,
	};
}
