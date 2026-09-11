using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Turns the provider's flat element list into the tree it describes, in place.
/// <para>
/// The list is the framework's own enumeration order, not a walk from the root, so a child can
/// arrive before its parent and every parent has to be resolved after every row exists. Everything
/// else here is about a snapshot that does not quite describe a tree -- a handle listed twice, a
/// parent nobody listed, a parent that is its own descendant. None of those can be drawn, and all
/// three have the same wrong answer available: quietly drop the element, and hand back a tree that
/// reads as complete and is missing a subtree. They are put at the top and flagged instead.
/// </para>
/// </summary>
public static class XamlTreeBuilder
{
	/// <summary>
	/// How deep a freshly seen element is opened to. Three reaches past the window and its root
	/// panel to the content somebody came to look at, without unfolding a template's internals --
	/// which is most of the elements in any real app.
	/// </summary>
	public const int DefaultExpandDepth = 3;

	/// <summary>
	/// Brings an inspection's rows in line with a fresh read, keeping the row objects the new read
	/// still has so expansion and selection survive it.
	/// </summary>
	public static void Merge(
		XamlInspection inspection,
		IReadOnlyList<LiveXamlNode> nodes,
		int expandDepth = DefaultExpandDepth)
	{
		var listed = new Dictionary<ulong, LiveXamlNode>(nodes.Count);
		var ordered = new List<LiveXamlNode>(nodes.Count);

		// One row per handle. An element listed twice cannot be drawn in both places, and drawing it
		// in one of them silently is worse than taking the first listing and being consistent.
		foreach (var node in nodes)
		{
			if (!listed.TryAdd(node.Handle, node)) continue;

			ordered.Add(node);
		}

		var rows = new Dictionary<ulong, XamlNodeRow>(ordered.Count);
		var arrived = new List<XamlNodeRow>();

		foreach (var node in ordered)
		{
			var row = inspection.Row(node.Handle);

			if (row is null)
			{
				row = new XamlNodeRow(node);
				arrived.Add(row);
			}
			else
			{
				row.Update(node);
			}

			rows[node.Handle] = row;
		}

		var children = new Dictionary<ulong, List<LiveXamlNode>>();
		var roots = new List<LiveXamlNode>();

		foreach (var node in ordered)
		{
			var unplaceable = node.Parent != 0 && !Placeable(listed, node);
			rows[node.Handle].IsOrphan = unplaceable;

			if (node.Parent == 0 || unplaceable)
			{
				roots.Add(node);
				continue;
			}

			if (!children.TryGetValue(node.Parent, out var siblings))
			{
				siblings = [];
				children[node.Parent] = siblings;
			}

			siblings.Add(node);
		}

		foreach (var node in ordered)
		{
			Fill(rows[node.Handle].Children, Order(children.GetValueOrDefault(node.Handle), rows), rows[node.Handle]);
		}

		// After the children, so a row that has moved to the top has its parent cleared rather than
		// left pointing at where it used to hang.
		Fill(inspection.Roots, Order(roots, rows), null);

		inspection.Reindex(rows);

		// Only what has just appeared. An element somebody collapsed stays collapsed through every
		// refresh, which is the whole reason rows are merged rather than rebuilt.
		foreach (var row in arrived)
		{
			row.IsExpanded = Depth(row) < expandDepth;
		}
	}

	/// <summary>
	/// The chain from the root down to an element, the element last. What a reveal expands, so a
	/// pick in the running app lands somewhere a reader can see.
	/// </summary>
	public static IReadOnlyList<XamlNodeRow> Ancestors(XamlInspection inspection, ulong handle)
	{
		if (inspection.Row(handle) is not { } row) return [];

		var chain = new List<XamlNodeRow>();
		var seen = new HashSet<XamlNodeRow>();

		for (var at = row; at is not null && seen.Add(at); at = at.Parent)
		{
			chain.Add(at);
		}

		chain.Reverse();
		return chain;
	}

	/// <summary>
	/// Whether a node's parent is one this tree can hang it under: listed, and not a descendant of
	/// the node itself. The walk is bounded by its own visited set, because the thing it is looking
	/// for is a loop and following one is how this hangs instead of reporting it.
	/// </summary>
	private static bool Placeable(Dictionary<ulong, LiveXamlNode> listed, LiveXamlNode node)
	{
		var seen = new HashSet<ulong> { node.Handle };

		for (var at = node.Parent; at != 0;)
		{
			if (!listed.TryGetValue(at, out var parent)) return false;
			if (!seen.Add(at)) return false;

			at = parent.Parent;
		}

		return true;
	}

	/// <summary>Children in the order the framework holds them, with the handle breaking a tie.</summary>
	private static List<XamlNodeRow> Order(List<LiveXamlNode>? nodes, Dictionary<ulong, XamlNodeRow> rows) =>
		nodes is null
			? []
			: [.. nodes.OrderBy(node => node.ChildIndex).ThenBy(node => node.Handle).Select(node => rows[node.Handle])];

	/// <summary>
	/// Brings a bound collection to exactly these rows in exactly this order, moving what is already
	/// there rather than replacing it. A row that has moved between parents is removed from one
	/// collection and inserted into the other, and is the same object either side.
	/// </summary>
	private static void Fill(ObservableCollection<XamlNodeRow> into, List<XamlNodeRow> wanted, XamlNodeRow? parent)
	{
		// Backwards, because removing shifts every index after it.
		for (var index = into.Count - 1; index >= 0; index--)
		{
			if (!wanted.Contains(into[index])) into.RemoveAt(index);
		}

		for (var index = 0; index < wanted.Count; index++)
		{
			var row = wanted[index];
			var at = into.IndexOf(row);

			if (at < 0) into.Insert(index, row);
			else if (at != index) into.Move(at, index);

			row.Parent = parent;
		}
	}

	/// <summary>How far down a row sits, counting a root as zero.</summary>
	private static int Depth(XamlNodeRow row)
	{
		var depth = 0;
		var seen = new HashSet<XamlNodeRow> { row };

		for (var at = row.Parent; at is not null && seen.Add(at); at = at.Parent)
		{
			depth++;
		}

		return depth;
	}
}
