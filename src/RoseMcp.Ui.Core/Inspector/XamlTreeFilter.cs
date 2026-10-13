namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// What the XAML view's tree is narrowed to: text a row's name, type or address holds, only what live edits changed,
/// and only the app's own markup.
/// </summary>
/// <param name="Text">Text to find, matched without regard to case; empty finds everything.</param>
/// <param name="ChangedOnly">Only elements a live edit changed this session.</param>
/// <param name="JustMyXaml">Only elements the app's own markup declares, leaving out a control template's parts.</param>
public sealed record XamlTreeQuery(string Text, bool ChangedOnly, bool JustMyXaml)
{
	/// <summary>Everything, which is what a tree shows with no filter on.</summary>
	public static XamlTreeQuery All { get; } = new(string.Empty, ChangedOnly: false, JustMyXaml: false);

	/// <summary>Whether a filter that picks rows out is on: text to find, or only what changed.</summary>
	public bool Seeks => Text.Trim().Length > 0 || ChangedOnly;
}

/// <summary>What a filter did to a tree, for the line above it to say.</summary>
/// <param name="Matched">How many elements matched, the ones kept only to reach a match not counted.</param>
/// <param name="JustMyXamlApplied">
/// Whether Just my XAML narrowed anything: false where it was asked for and the tree carries no source info at all, so
/// it could not tell the app's markup from a template's.
/// </param>
public sealed record XamlTreeFiltered(int Matched, bool JustMyXamlApplied);

/// <summary>
/// Narrows a visual tree in place, by setting each row's <see cref="XamlNodeRow.IsShown"/>, rather than building a
/// second tree: the rows are what remember expansion and selection, and a copy would forget both every keystroke.
/// <para>
/// A row stays when it matches, and so does every row above a match, or the match would be shown with nothing to say
/// where it is. A seeking filter -- text, or only what changed -- also opens the way down to each match, since a match
/// inside a closed row is one nobody can see; Just my XAML on its own opens nothing, being on from the start and
/// meaning the whole tree.
/// </para>
/// </summary>
public static class XamlTreeFilter
{
	/// <summary>Narrows <paramref name="inspection"/>'s tree to <paramref name="query"/>.</summary>
	public static XamlTreeFiltered Apply(XamlInspection inspection, XamlTreeQuery query)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		ArgumentNullException.ThrowIfNull(query);

		// Without a single source file in the tree there is no telling the app's markup from a template's, and leaving
		// out everything that has none would empty the tree of an app built without source info -- which reads as an
		// app with no UI rather than as a filter that cannot work.
		var anySource = inspection.Index.Values.Any(row => row.File is { Length: > 0 });
		var justMine = query.JustMyXaml && anySource;
		var text = query.Text.Trim();

		var matched = 0;
		foreach (var root in inspection.Roots)
		{
			matched += Visit(root, query, text, justMine);
		}

		return new XamlTreeFiltered(matched, JustMyXamlApplied: justMine || !query.JustMyXaml);
	}

	/// <summary>
	/// Whether an element was declared in the app's own markup, by the URI its source info names.
	/// <para>
	/// The same question the in-app toolbar asks when Just my XAML decides a pick, asked the same way, because the two
	/// disagreeing would have the tree hide the element a click in the app selects. Under <c>ms-appx:</c> the owner is
	/// the authority, and an empty one is the app's own package; WinUI 2's themes are <c>ms-appx:</c> too, under the
	/// framework's package name. Under <c>ms-resource:</c> the authority says nothing, and a dictionary from a
	/// referenced assembly names it as <c>;component/</c>. No source info at all is not the app's.
	/// </para>
	/// </summary>
	public static bool IsAppMarkup(string? file)
	{
		if (file is not { Length: > 0 } source) return false;
		if (source.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase)) return true;
		if (source.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase)) return false;
		if (!source.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return false;

		return !source.Contains(";component/", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Shows or hides <paramref name="row"/> and everything under it, and answers how many matched there.</summary>
	private static int Visit(XamlNodeRow row, XamlTreeQuery query, string text, bool justMine)
	{
		var below = 0;
		foreach (var child in row.Children)
		{
			below += Visit(child, query, text, justMine);
		}

		var matches = Matches(row, query, text, justMine);
		var leadsToMatch = below > 0;

		row.IsShown = matches || leadsToMatch;

		if (query.Seeks && leadsToMatch) row.IsExpanded = true;

		return below + (matches ? 1 : 0);
	}

	private static bool Matches(XamlNodeRow row, XamlTreeQuery query, string text, bool justMine)
	{
		if (query.ChangedOnly && !row.IsChanged) return false;
		if (justMine && !IsAppMarkup(row.File)) return false;
		if (text.Length == 0) return true;

		return Holds(row.Label, text)
			|| Holds(row.TypeName, text)
			|| Holds(row.Address, text)
			|| Holds(row.Where, text);
	}

	private static bool Holds(string? value, string text) =>
		value is { Length: > 0 } && value.Contains(text, StringComparison.OrdinalIgnoreCase);
}
