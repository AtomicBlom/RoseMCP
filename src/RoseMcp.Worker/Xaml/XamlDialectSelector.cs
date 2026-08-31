using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Works out which XAML flavour a project is written in.
/// <para>
/// Decided per project rather than per file: one markup compiler serves an assembly, so a project is
/// WPF or UWP or WinUI throughout, and deciding per file would let two pages in one project disagree
/// about their base namespaces.
/// </para>
/// <para>
/// Neither of the two obvious signals works, both checked rather than assumed. All three frameworks
/// use the same presentation namespace URI verbatim. And UWP on .NET 10 defines no UWP-specific
/// preprocessor symbol -- only WINDOWS and the WINDOWS10_0_* family -- so the symbols that exist on
/// legacy UWP are corroboration at best.
/// </para>
/// </summary>
public static class XamlDialectSelector
{
	public static IReadOnlyList<IXamlDialect> All { get; } =
		[WindowsXamlDialect.WinUi, WindowsXamlDialect.Uwp, WpfXamlDialect.Instance];

	public static XamlDialectChoice Select(Compilation compilation, IReadOnlyList<XamlDocument> documents)
	{
		var referenced = All
			.Where(dialect => compilation.GetTypeByMetadataName(dialect.MarkerTypeName) is not null)
			.ToArray();

		if (referenced.Length == 0)
		{
			return new XamlDialectChoice
			{
				Dialect = null,
				Reason = "no XAML framework is referenced",
				WasAmbiguous = false,
			};
		}

		if (referenced.Length == 1)
		{
			return new XamlDialectChoice
			{
				Dialect = referenced[0],
				Reason = $"references {referenced[0].MarkerTypeName}",
				WasAmbiguous = false,
			};
		}

		// More than one framework is referenced, which happens mid-migration. Ask the hand-written
		// code, which was written against one of them and says so in its using directives.
		if (ByUsingCensus(compilation, referenced) is { } byCensus) return byCensus;

		// Failing that, ask the markup: WPF has no using: form, and the other two have no
		// clr-namespace: form, so the prefixes separate them even when both are referenced.
		if (ByMarkupSyntax(documents, referenced) is { } byMarkup) return byMarkup;

		return new XamlDialectChoice
		{
			Dialect = referenced[0],
			Reason = $"{referenced.Length} frameworks referenced and nothing distinguished them; "
				+ $"assumed {referenced[0].Name}",
			WasAmbiguous = true,
		};
	}

	private static XamlDialectChoice? ByUsingCensus(Compilation compilation, IReadOnlyList<IXamlDialect> candidates)
	{
		var counts = candidates.ToDictionary(dialect => dialect, _ => 0);

		foreach (var tree in compilation.SyntaxTrees)
		{
			if (!tree.FilePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) continue;

			foreach (var directive in tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
			{
				var name = directive.Name?.ToString();
				if (name is null) continue;

				foreach (var dialect in candidates.Where(dialect =>
					name.StartsWith(dialect.UsingNamespaceRoot, StringComparison.Ordinal)))
				{
					counts[dialect]++;
				}
			}
		}

		var ranked = counts.OrderByDescending(pair => pair.Value).ToArray();
		if (ranked[0].Value == 0 || ranked[0].Value == ranked[1].Value) return null;

		return new XamlDialectChoice
		{
			Dialect = ranked[0].Key,
			Reason = $"code-behind imports {ranked[0].Key.UsingNamespaceRoot} in {ranked[0].Value} place(s)",
			WasAmbiguous = true,
		};
	}

	private static XamlDialectChoice? ByMarkupSyntax(
		IReadOnlyList<XamlDocument> documents,
		IReadOnlyList<IXamlDialect> candidates)
	{
		var usesClrNamespace = documents.Any(document => document.NamedElements
			.Append(new XamlNamedElement { Name = string.Empty, Type = document.RootType })
			.Any(element => element.Type.NamespaceUri.StartsWith("clr-namespace:", StringComparison.Ordinal)));

		var wanted = usesClrNamespace ? "WPF" : null;
		if (wanted is null) return null;

		var match = candidates.FirstOrDefault(dialect => dialect.Name == wanted);
		if (match is null) return null;

		return new XamlDialectChoice
		{
			Dialect = match,
			Reason = "markup uses clr-namespace: prefixes, which only WPF accepts",
			WasAmbiguous = true,
		};
	}
}
