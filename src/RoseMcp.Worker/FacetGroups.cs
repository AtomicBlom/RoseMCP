namespace RoseMcp.Worker;

/// <summary>
/// How a list too long to give divides along one facet: a group per value, most first, ties broken by
/// the value so the same answer describes itself the same way twice.
/// <para>
/// The one way the worker's overflowing lists describe themselves -- references, diagnostics and
/// symbol matches -- so the rule each depends on is written once: every group is keyed
/// by the value the narrowing argument of the same name takes, so a group read in one answer is the
/// question the next call asks.
/// </para>
/// </summary>
public static class FacetGroups
{
	/// <summary>
	/// The items grouped by <paramref name="key"/>, most first, items with no value for the facet left
	/// out, and each group made by <paramref name="group"/> from its value and its items.
	/// </summary>
	/// <param name="items">What is being described.</param>
	/// <param name="key">The facet's value on one item, or null where the item has none.</param>
	/// <param name="group">One group, from its value and the items carrying it.</param>
	public static IReadOnlyList<TGroup> Of<TItem, TGroup>(
		IEnumerable<TItem> items,
		Func<TItem, string?> key,
		Func<string, IReadOnlyList<TItem>, TGroup> group) =>
		[
			.. items
				.Select(item => (Item: item, Key: key(item)))
				.Where(pair => pair.Key is not null)
				.GroupBy(pair => pair.Key!, StringComparer.Ordinal)
				.OrderByDescending(grouping => grouping.Count())
				.ThenBy(grouping => grouping.Key, StringComparer.Ordinal)
				.Select(grouping => group(grouping.Key, [.. grouping.Select(pair => pair.Item)])),
		];

	/// <summary>
	/// At most <paramref name="cap"/> of <paramref name="groups"/>, for a facet with no natural bound --
	/// members, files -- where the count of all of them is said beside the list.
	/// </summary>
	public static IReadOnlyList<TGroup> Capped<TGroup>(IReadOnlyList<TGroup> groups, int cap) =>
		groups.Count > cap ? [.. groups.Take(cap)] : groups;
}
