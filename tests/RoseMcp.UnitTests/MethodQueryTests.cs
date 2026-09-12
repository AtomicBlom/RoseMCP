using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// How a typed query ranks the methods of a target's loaded modules.
/// <para>
/// The ordering is the feature. A list that contains the method somebody wants and puts it below
/// forty methods of types whose namespace happens to share those letters is a list they abandon for
/// typing the name by hand, which is the thing this exists to replace.
/// </para>
/// </summary>
public sealed class MethodQueryTests
{
	[Test]
	[Arguments("MyApp.Widget", "Refresh", "Refresh", 0)]
	[Arguments("MyApp.Widget", "Refresh", "refresh", 0)]
	[Arguments("MyApp.Widget", "RefreshAll", "Refresh", 1)]
	[Arguments("MyApp.Widget", "ForceRefresh", "Refresh", 2)]
	[Arguments("MyApp.Refresher", "Run", "Refresh", 3)]
	public void A_method_name_match_outranks_a_type_name_match(
		string typeName,
		string methodName,
		string query,
		int expected)
	{
		Assert.Equal(expected, MethodQuery.Rank(typeName, methodName, query));
	}

	/// <summary>
	/// A qualified query is the pieces in order, anywhere in the name, because the part somebody
	/// remembers is the type and the method rather than the namespace they start with.
	/// </summary>
	[Test]
	public void The_pieces_of_a_query_match_in_order()
	{
		Assert.Equal(0, MethodQuery.Rank("MyApp.Ui.Widget", "Refresh", "Widget.Refresh"));
		Assert.Equal(0, MethodQuery.Rank("MyApp.Ui.Widget", "Refresh", "Ui.Widget.Refresh"));

		// Out of order is not a match: the order is the only thing separating a qualified query from
		// a bag of letters, and without it Widget.Refresh would find Refresher.Widget.
		Assert.Null(MethodQuery.Rank("MyApp.Ui.Widget", "Refresh", "Refresh.Widget"));
	}

	/// <summary>A trailing dot is what somebody has typed on the way to the next piece, not a filter.</summary>
	[Test]
	public void A_trailing_dot_changes_nothing()
	{
		Assert.Equal(MethodQuery.Rank("MyApp.Widget", "Refresh", "Widget"), MethodQuery.Rank("MyApp.Widget", "Refresh", "Widget."));
	}

	[Test]
	[Arguments(null)]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments("R")]
	[Arguments(".")]
	public void Nothing_worth_searching_for_is_refused(string? query)
	{
		Assert.False(MethodQuery.IsWorthSearching(query));
	}

	[Test]
	public void Two_characters_are_enough_to_search_for()
	{
		Assert.True(MethodQuery.IsWorthSearching("Re"));
		Assert.True(MethodQuery.IsWorthSearching("Widget.Refresh"));
	}

	/// <summary>
	/// A query nothing matches returns nothing rather than everything. It reads as obvious and is the
	/// failure that would show as an autocomplete listing the whole framework the moment a name is
	/// mistyped.
	/// </summary>
	[Test]
	public void A_query_that_matches_nothing_ranks_nothing()
	{
		Assert.Null(MethodQuery.Rank("MyApp.Widget", "Refresh", "Sprocket"));
		Assert.Null(MethodQuery.Rank("MyApp.Widget", "Refresh", ""));
	}
}
