using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RoseMcp.XamlDiff;

/// <summary>
/// A minimal XAML diff: parse two versions, match elements by <c>x:Name</c> (falling back to element
/// type and sibling position), and emit the smallest set of edits between them for the live tree to
/// apply. It handles property changes on any element -- named or not -- including attached properties,
/// and detects added and removed children as structural edits carrying the child's markup. A named
/// element is addressed as <c>#name</c>; an unnamed one by a path anchored at its nearest named
/// ancestor (or the root), each segment <c>Type[index]</c> among same-type siblings. Reordering is not
/// detected as a move; the elements' property changes are still diffed in place.
/// </summary>
public static class XamlDiff
{
	private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

	// The value type to materialise for a given property, so an apply side's CreateInstance knows what
	// to build from the string. Brush and Double properties are the reliable, common cases.
	private static readonly Dictionary<string, string> ValueTypes = new(StringComparer.Ordinal)
	{
		["Background"] = "Windows.UI.Xaml.Media.SolidColorBrush",
		["Foreground"] = "Windows.UI.Xaml.Media.SolidColorBrush",
		["BorderBrush"] = "Windows.UI.Xaml.Media.SolidColorBrush",
		["Fill"] = "Windows.UI.Xaml.Media.SolidColorBrush",
		["Stroke"] = "Windows.UI.Xaml.Media.SolidColorBrush",
		["Width"] = "Windows.Foundation.Double",
		["Height"] = "Windows.Foundation.Double",
		["Opacity"] = "Windows.Foundation.Double",
		["FontSize"] = "Windows.Foundation.Double",
		["Margin"] = "Windows.UI.Xaml.Thickness",
		["Padding"] = "Windows.UI.Xaml.Thickness",
		["BorderThickness"] = "Windows.UI.Xaml.Thickness",

		// A single number, so InferValueType would otherwise call it a Double -- which builds fine and
		// then fails at SetProperty. The provider recovers from that by asking the property its own
		// type, but getting it right here means the common case takes no round trip.
		["CornerRadius"] = "Windows.UI.Xaml.CornerRadius",
	};

	public static XamlDiffResult Compute(string oldXaml, string newXaml)
	{
		var edits = new List<XamlEdit>();
		var notes = new List<string>();

		DiffElement(XElement.Parse(oldXaml), XElement.Parse(newXaml), edits, notes);
		return new XamlDiffResult { Edits = edits, Notes = notes };
	}

	/// <summary>
	/// Whether <paramref name="markup"/> is something this engine can diff, with the parser's own
	/// reason when it is not.
	/// <para>
	/// Here rather than in the callers so that one parser decides. A caller running its own
	/// <c>XElement.Parse</c> would be answering a slightly different question, and could accept markup
	/// this cannot diff or refuse markup it can.
	/// </para>
	/// <para>
	/// It exists because a continuous apply records what it reads as the baseline for the next one, and
	/// recording markup that does not parse leaves every later apply diffing against something
	/// unparseable -- reporting a parse error about a file the caller has since fixed, with no way to
	/// say so.
	/// </para>
	/// </summary>
	public static bool Parses(string markup, out string? reason)
	{
		try
		{
			XElement.Parse(markup);
			reason = null;

			return true;
		}
		catch (XmlException exception)
		{
			reason = exception.Message;

			return false;
		}
	}

	private static void DiffElement(XElement oldElement, XElement newElement, List<XamlEdit> edits, List<string> notes)
	{
		DiffProperties(AddressOf(newElement), oldElement, newElement, edits);

		var oldChildren = oldElement.Elements().ToList();
		var newChildren = newElement.Elements().ToList();
		var usedOld = new bool[oldChildren.Count];
		var index = 0;

		foreach (var newChild in newChildren)
		{
			// A dotted local name is property-element syntax -- <Page.Resources>, <Grid.RowDefinitions> --
			// which writes a property and is never a child of anything in the visual tree. Two things
			// follow, and both used to be wrong.
			//
			// It must not advance the child position. It did, so an element added after a
			// <Grid.RowDefinitions> was given an index counting something that is not its sibling, and
			// went in at the wrong place or nowhere.
			//
			// And it cannot be walked into as though it held children. Doing that produced addresses like
			// `Grid[0]/Grid.Resources[0]/SolidColorBrush[0]`, which nothing can resolve -- so changing a
			// brush in a resource dictionary failed with a message about a missing element, which is the
			// wrong problem described confidently.
			if (newChild.Name.LocalName.Contains('.'))
			{
				DiffPropertyElement(newElement, newChild, oldChildren, usedOld, edits, notes);
				continue;
			}

			var matchIndex = FindMatch(newChild, oldChildren, usedOld);
			if (matchIndex < 0)
			{
				var markup = newChild.ToString(SaveOptions.DisableFormatting);

				edits.Add(new XamlEdit
				{
					Kind = XamlEditKind.AddChild,
					Target = AddressOf(newElement),
					Payload = markup,
					Index = index,
				});

				// Said rather than left to be discovered. An x:Name belongs to a namescope the markup
				// compiler filled in, and nothing settable at runtime puts an element into one -- so an
				// element added here arrives without its name, and "#thatName" will not find it. Its
				// path will. The surprise would otherwise land later, on a call that looks unrelated.
				if (XamlMaterialiser.NamesAnything(markup))
				{
					notes.Add($"The element added under {AddressOf(newElement)} carries an x:Name, which a live "
						+ "add cannot give it: names come from a namescope the markup compiler built. Address it "
						+ "by its path instead.");
				}
			}
			else
			{
				usedOld[matchIndex] = true;
				DiffElement(oldChildren[matchIndex], newChild, edits, notes);
			}

			index++;
		}

		for (var i = 0; i < oldChildren.Count; i++)
		{
			if (usedOld[i]) continue;

			// A property that has gone is not a child that has gone, and asking the tree to remove one
			// would look for an element of that name and not find it.
			if (oldChildren[i].Name.LocalName.Contains('.')) continue;

			edits.Add(new XamlEdit { Kind = XamlEditKind.RemoveChild, Target = AddressOf(oldChildren[i]) });
		}
	}

	/// <summary>
	/// A property written in element form. A resources block is understood; anything else is reported
	/// rather than walked into, because walking in built an address for something that is not an element
	/// and the apply then failed naming a missing element instead of an edit it does not do.
	/// </summary>
	private static void DiffPropertyElement(
		XElement owner,
		XElement newBlock,
		List<XElement> oldChildren,
		bool[] usedOld,
		List<XamlEdit> edits,
		List<string> notes)
	{
		var matchIndex = oldChildren.FindIndex(child => child.Name == newBlock.Name);
		if (matchIndex >= 0) usedOld[matchIndex] = true;

		var oldBlock = matchIndex >= 0 ? oldChildren[matchIndex] : null;

		if (newBlock.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal))
		{
			DiffResources(owner, oldBlock, newBlock, edits, notes);
			return;
		}

		var unchanged = oldBlock is not null
			&& oldBlock.ToString(SaveOptions.DisableFormatting) == newBlock.ToString(SaveOptions.DisableFormatting);

		if (unchanged) return;

		notes.Add($"{newBlock.Name.LocalName} on {AddressOf(owner)} changed, and a property written in element "
			+ "form is not applied live. An element's own attributes and its resources are.");
	}

	/// <summary>
	/// Resources are keyed rather than positional, so they are matched by <c>x:Key</c> and never by where
	/// they sit -- reordering a dictionary means nothing, and two resources of one type are told apart by
	/// nothing else.
	/// <para>
	/// The whole resource is replaced rather than its attributes edited, because that is the shape of
	/// what the framework offers: <c>ReplaceResource</c> swaps what a key resolves to. Editing the
	/// existing instance's properties would be a different thing with different consequences, since one
	/// brush object can be behind a dozen keys.
	/// </para>
	/// </summary>
	private static void DiffResources(
		XElement owner,
		XElement? oldBlock,
		XElement newBlock,
		List<XamlEdit> edits,
		List<string> notes)
	{
		var before = Keyed(oldBlock);
		var after = Keyed(newBlock);

		foreach (var (key, resource) in after)
		{
			var markup = resource.ToString(SaveOptions.DisableFormatting);

			if (!before.TryGetValue(key, out var was))
			{
				notes.Add($"The resource '{key}' on {AddressOf(owner)} is new. Adding a resource is not applied "
					+ "live yet; changing one that is already there is.");
				continue;
			}

			if (was.ToString(SaveOptions.DisableFormatting) == markup) continue;

			edits.Add(new XamlEdit
			{
				Kind = XamlEditKind.SetResource,
				Target = AddressOf(owner),
				Property = key,
				Payload = markup,
			});
		}

		foreach (var key in before.Keys)
		{
			if (after.ContainsKey(key)) continue;

			notes.Add($"The resource '{key}' was removed from {AddressOf(owner)}. Removing a resource is not "
				+ "applied live yet.");
		}
	}

	/// <summary>
	/// The resources in a block, by their <c>x:Key</c>. A block may hold its resources directly or wrap
	/// them in an explicit <c>ResourceDictionary</c>; both spellings mean the same dictionary, and a
	/// reader that understood only one would silently find no resources in half the markup it met.
	/// </summary>
	private static Dictionary<string, XElement> Keyed(XElement? block)
	{
		var keyed = new Dictionary<string, XElement>(StringComparer.Ordinal);
		if (block is null) return keyed;

		var children = block.Elements().ToList();
		if (children is [{ } only] && only.Name.LocalName == "ResourceDictionary")
		{
			children = only.Elements().ToList();
		}

		foreach (var resource in children)
		{
			if (resource.Attribute(XName.Get("Key", XamlNamespace))?.Value is { } key) keyed[key] = resource;
		}

		return keyed;
	}

	private static void DiffProperties(string target, XElement oldElement, XElement newElement, List<XamlEdit> edits)
	{
		var oldProps = Properties(oldElement);
		var newProps = Properties(newElement);

		foreach (var (property, value) in newProps)
		{
			if (oldProps.TryGetValue(property, out var previous) && previous == value) continue;

			edits.Add(new XamlEdit
			{
				Kind = XamlEditKind.SetProperty,
				Target = target,
				Property = property,
				ValueType = InferValueType(property, value),
				Value = value,
			});
		}

		foreach (var property in oldProps.Keys)
		{
			if (!newProps.ContainsKey(property))
			{
				edits.Add(new XamlEdit { Kind = XamlEditKind.ClearProperty, Target = target, Property = property });
			}
		}
	}

	// Property attributes, including attached (dotted) ones; excludes namespace declarations and x:*
	// directives, which are not runtime-settable properties.
	internal static Dictionary<string, string> Properties(XElement element)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var attribute in element.Attributes())
		{
			if (attribute.IsNamespaceDeclaration) continue;
			if (attribute.Name.Namespace == XamlNamespace) continue;

			result[attribute.Name.LocalName] = attribute.Value;
		}

		return result;
	}

	private static int FindMatch(XElement newChild, List<XElement> oldChildren, bool[] used)
	{
		var name = NameOf(newChild);
		if (name is not null)
		{
			for (var i = 0; i < oldChildren.Count; i++)
			{
				if (!used[i] && NameOf(oldChildren[i]) == name) return i;
			}

			return -1; // A named element only matches the same name; otherwise it is genuinely new.
		}

		for (var i = 0; i < oldChildren.Count; i++)
		{
			if (!used[i] && oldChildren[i].Name == newChild.Name && NameOf(oldChildren[i]) is null) return i;
		}

		return -1;
	}

	/// <summary>A named element is <c>#name</c>; an unnamed one is a path anchored at its nearest named ancestor.</summary>
	private static string AddressOf(XElement element)
	{
		if (NameOf(element) is { } named) return $"#{named}";

		var segments = new List<string>();
		var current = element;
		while (current is not null)
		{
			segments.Add(Segment(current));
			if (NameOf(current) is not null) break; // Anchor at the nearest named ancestor.
			current = current.Parent;
		}

		segments.Reverse();
		return string.Join("/", segments);
	}

	private static string Segment(XElement element)
	{
		var name = NameOf(element);
		if (name is not null) return $"#{name}";

		// Counted over the local name rather than the qualified one, because the local name is the only
		// part the live tree has: an element there carries a CLR type name and no XML namespace at all,
		// so an index that separated `local:Border` from `Border` would rest on a distinction the
		// resolver cannot see. It counted the qualified name and printed the local one, so each of those
		// two counted only its own kind and both came out `Border[0]` -- one address for two elements,
		// which resolves to whichever the walk reaches first and reports a successful edit either way.
		// Counting the way the resolver counts is what keeps the two halves in agreement.
		//
		// Property-element syntax needs no special case here: `Grid.RowDefinitions` has a dot in its
		// local name and a type's never does, so it can equal no segment and is passed over.
		var index = element.ElementsBeforeSelf().Count(sibling => sibling.Name.LocalName == element.Name.LocalName);
		return $"{element.Name.LocalName}[{index}]";
	}

	internal static string? NameOf(XElement element)
		=> element.Attribute(XName.Get("Name", XamlNamespace))?.Value ?? element.Attribute("Name")?.Value;

	internal static string InferValueType(string property, string value)
	{
		if (ValueTypes.TryGetValue(property, out var mapped)) return mapped;

		if (value.StartsWith('#') || value.StartsWith("{StaticResource", StringComparison.Ordinal)) return "Windows.UI.Xaml.Media.SolidColorBrush";
		if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return "Windows.Foundation.Double";
		if (bool.TryParse(value, out _)) return "Windows.Foundation.Boolean";
		if (LooksLikeThickness(value)) return "Windows.UI.Xaml.Thickness";

		return string.Empty; // Unknown: the apply side sets it as a string.
	}

	private static bool LooksLikeThickness(string value)
	{
		var parts = value.Split(',');
		return parts.Length is 2 or 4
			&& parts.All(part => double.TryParse(part.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _));
	}
}
