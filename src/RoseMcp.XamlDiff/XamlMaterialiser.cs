using System.Xml.Linq;

namespace RoseMcp.XamlDiff;

/// <summary>
/// Takes the markup of an added element apart into the primitive steps a live tree can be built
/// from.
/// <para>
/// It exists because the diagnostics API cannot apply markup at all. <c>CreateInstance</c> builds one
/// object from a type name, <c>SetProperty</c> sets one property on one instance, and <c>AddChild</c>
/// puts one instance into one collection -- so markup has to be decomposed, and the half of this
/// system that already holds an XML parser should be the one doing it rather than the native provider
/// growing one. Being pure, it is unit tested; the host it feeds cannot be, since that project
/// targets Windows and the test projects cannot see inside it.
/// </para>
/// <para>
/// The order is the part worth protecting. An element is created, then given its properties, then
/// given its children -- and the finished subtree is attached to the app in the very last step. The
/// framework therefore never lays out or renders a half-built element, and nothing watching the tree
/// can observe one.
/// </para>
/// </summary>
public static class XamlMaterialiser
{
	/// <summary>
	/// The steps that build <paramref name="markup"/> and put it under <paramref name="parentTarget"/>
	/// at <paramref name="index"/>. Throws if the markup does not parse, which is the caller's cue
	/// that there is nothing to apply.
	/// </summary>
	public static IReadOnlyList<XamlStep> Steps(string markup, string parentTarget, int index)
	{
		var steps = new List<XamlStep>();
		var slots = 0;
		var root = Build(XElement.Parse(markup), steps, ref slots);

		steps.Add(new XamlStep
		{
			Kind = XamlStepKind.AddChild,
			Target = parentTarget,
			Child = root,
			Index = index,
		});

		return steps;
	}

	/// <summary>
	/// The steps that build <paramref name="markup"/> and leave it in <see cref="RootSlot"/>, attached
	/// to nothing. For a resource, which goes behind a key rather than into a parent's children -- so
	/// the caller finishes the job with whatever puts it there.
	/// </summary>
	public static IReadOnlyList<XamlStep> Unattached(string markup)
	{
		var steps = new List<XamlStep>();
		var slots = 0;
		Build(XElement.Parse(markup), steps, ref slots);

		return steps;
	}

	/// <summary>
	/// Where <see cref="Unattached"/> leaves the thing it built. The root is created first and slots are
	/// handed out in creation order, so this is a fact about the ordering rather than a convention -- and
	/// naming it here stops the caller hard-coding it and stops the two drifting.
	/// </summary>
	public const string RootSlot = "$0";

	/// <summary>Whether markup names an element, which is worth saying because the name will not survive.</summary>
	public static bool NamesAnything(string markup)
	{
		try
		{
			return XElement.Parse(markup).DescendantsAndSelf().Any(element => XamlDiff.NameOf(element) is not null);
		}
		catch (System.Xml.XmlException)
		{
			return false;
		}
	}

	private static string Build(XElement element, List<XamlStep> steps, ref int slots)
	{
		var slot = $"${slots++}";

		steps.Add(new XamlStep
		{
			Kind = XamlStepKind.Create,
			Target = slot,
			TypeName = element.Name.LocalName,
		});

		// The same rules the diff reads properties by, so an added element is given exactly what a
		// changed one would have been. x:Name is among the directives those rules drop, and it stays
		// dropped: a name belongs to a namescope the markup compiler filled in, and there is nothing
		// to set at runtime that would put an element into one. So an element added with an x:Name
		// cannot be reached by "#name" afterwards -- its path still reaches it, and the diff says so.
		foreach (var (property, value) in XamlDiff.Properties(element))
		{
			steps.Add(new XamlStep
			{
				Kind = XamlStepKind.SetProperty,
				Target = slot,
				Property = property,
				ValueType = XamlDiff.InferValueType(property, value),
				Value = value,
			});
		}

		var child = 0;
		foreach (var nested in element.Elements())
		{
			// Property-element syntax is not a child. <Grid.RowDefinitions> is a way of writing a
			// property, and adding it as one would fail somewhere that reads like a fault in the
			// element beside it. A type's local name never carries a dot, so this is the whole test.
			if (nested.Name.LocalName.Contains('.')) continue;

			var nestedSlot = Build(nested, steps, ref slots);

			steps.Add(new XamlStep
			{
				Kind = XamlStepKind.AddChild,
				Target = slot,
				Child = nestedSlot,
				Index = child++,
			});
		}

		return slot;
	}
}
