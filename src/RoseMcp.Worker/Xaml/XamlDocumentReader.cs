using System.Xml;
using System.Xml.Linq;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Reads the little of a XAML file that matters for making its code-behind compile.
/// <para>
/// XAML is XML, so this is a tree walk rather than anything resembling the real markup compiler. It
/// answers three questions -- which class, which base type, which named elements -- and ignores the
/// rest of the markup entirely.
/// </para>
/// </summary>
public static class XamlDocumentReader
{
	/// <summary>
	/// Elements whose contents are a template rather than part of the visual tree. Names inside one
	/// belong to that template's namescope and get no field, so descending into them would invent
	/// members the real generator does not produce.
	/// </summary>
	private static readonly HashSet<string> TemplateElements = new(StringComparer.Ordinal)
	{
		"DataTemplate",
		"ControlTemplate",
		"ItemsPanelTemplate",
		"ItemContainerTemplate",
		"Style",
	};

	/// <summary>Null when the file is not XAML we can read, which is reported rather than guessed at.</summary>
	public static XamlDocument? Read(string path, string text)
	{
		XDocument document;
		try
		{
			// A BOM survives into the string when the file is read as text, and the XML parser
			// rejects it as content before the root element.
			document = XDocument.Parse(text.TrimStart('﻿', ' ', '\t', '\r', '\n'));
		}
		catch (XmlException)
		{
			return null;
		}

		if (document.Root is not { } root) return null;

		var named = new List<XamlNamedElement>();

		// The root is the class, and also a field when the markup names it -- which the real
		// generator does emit, and which reading the real output is how we found out.
		if (NameOf(root) is { Length: > 0 } rootName)
		{
			named.Add(new XamlNamedElement
			{
				Name = rootName,
				Type = new XamlTypeName(root.Name.NamespaceName, root.Name.LocalName),
				Modifier = ModifierOf(root),
				IsRoot = true,
			});
		}

		Collect(root, named);

		return new XamlDocument
		{
			Path = path,
			ClassName = (string?)root.Attribute(XName.Get("Class", XamlTypeName.LanguageNamespace)),
			RootType = new XamlTypeName(root.Name.NamespaceName, root.Name.LocalName),
			NamedElements = named,

			// Looked for in the text rather than the tree: a compiled binding can appear in an
			// attribute value or in nested markup, and either one makes the member exist.
			UsesCompiledBindings = text.Contains("{x:Bind", StringComparison.Ordinal),
		};
	}

	private static void Collect(XElement element, List<XamlNamedElement> named)
	{
		foreach (var child in element.Elements())
		{
			var localName = child.Name.LocalName;

			// A property element -- Grid.RowDefinitions, VisualStateManager.VisualStateGroups,
			// Page.Resources -- is not a type and cannot be named, but the elements under it can be.
			// Resources included: a keyed resource contributes nothing because it has no name, while
			// a named one gets a field exactly like any other element. Storyboards are usually
			// declared this way, and they were most of what an earlier version of this missed.
			if (localName.Contains('.', StringComparison.Ordinal))
			{
				Collect(child, named);

				continue;
			}

			if (TemplateElements.Contains(localName)) continue;

			if (NameOf(child) is { Length: > 0 } name)
			{
				named.Add(new XamlNamedElement
				{
					Name = name,
					Type = new XamlTypeName(child.Name.NamespaceName, localName),
					Modifier = ModifierOf(child),
				});
			}

			Collect(child, named);
		}
	}

	/// <summary>
	/// x:FieldModifier, restricted to the accessibilities C# will accept in this position so odd
	/// markup cannot produce a file that will not parse. Null when the markup does not say, which
	/// leaves the choice to the dialect rather than assuming one framework's default for all of them.
	/// </summary>
	private static string? ModifierOf(XElement element)
	{
		var declared = (string?)element.Attribute(XName.Get("FieldModifier", XamlTypeName.LanguageNamespace));

		return declared?.Trim().ToLowerInvariant() switch
		{
			"public" => "public",
			"internal" => "internal",
			"private" => "private",
			"protected" => "protected",
			"protected internal" => "protected internal",
			_ => null,
		};
	}

	/// <summary>
	/// x:Name, or the runtime Name property which the frameworks treat as equivalent for field
	/// generation. Taking both risks a field the real generator omits; the emitter's rule about
	/// never redeclaring an existing member is what keeps that from becoming an error.
	/// </summary>
	private static string? NameOf(XElement element) =>
		(string?)element.Attribute(XName.Get("Name", XamlTypeName.LanguageNamespace))
			?? (string?)element.Attribute("Name");
}
