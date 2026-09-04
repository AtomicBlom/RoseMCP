using System.Globalization;
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

	private static void DiffElement(XElement oldElement, XElement newElement, List<XamlEdit> edits, List<string> notes)
	{
		DiffProperties(AddressOf(newElement), oldElement, newElement, edits);

		var oldChildren = oldElement.Elements().ToList();
		var newChildren = newElement.Elements().ToList();
		var usedOld = new bool[oldChildren.Count];
		var index = 0;

		foreach (var newChild in newChildren)
		{
			var matchIndex = FindMatch(newChild, oldChildren, usedOld);
			if (matchIndex < 0)
			{
				edits.Add(new XamlEdit
				{
					Kind = XamlEditKind.AddChild,
					Target = AddressOf(newElement),
					Payload = newChild.ToString(SaveOptions.DisableFormatting),
					Index = index,
				});
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
			if (!usedOld[i])
			{
				edits.Add(new XamlEdit { Kind = XamlEditKind.RemoveChild, Target = AddressOf(oldChildren[i]) });
			}
		}
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
	private static Dictionary<string, string> Properties(XElement element)
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

	private static string? NameOf(XElement element)
		=> element.Attribute(XName.Get("Name", XamlNamespace))?.Value ?? element.Attribute("Name")?.Value;

	private static string InferValueType(string property, string value)
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
