using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Finding the module that declares a type, which is how a location written without its assembly is
/// bound. The modules are compiled under names chosen so that reading the module off the type's
/// namespace would pick the wrong one, or none.
/// </summary>
public sealed class TypeOwnersTests
{
	private const string ProductCore = "namespace Product.Core; public class Widget { public void Refresh() { } public class Part { public void Draw() { } } }";

	/// <summary>
	/// An assembly named apart from its namespaces, beside one named for the type's first segment: the
	/// module declaring the type is the answer, and the one a guess from the name would pick is not.
	/// </summary>
	[Test]
	public void A_type_is_found_in_the_module_that_declares_it_whatever_the_module_is_called()
	{
		using var declaring = CompiledModule.Of(ProductCore, "Pc.Core");
		using var misleading = CompiledModule.Of("namespace Product; public class Other { }", "Product");

		var owners = TypeOwners.Of([misleading.ModulePath, declaring.ModulePath], "Product.Core.Widget", assembly: null);

		Assert.Equal(new[] { declaring.ModulePath }, owners);
	}

	/// <summary>A nested type is spelled with a plus, as metadata spells it, and found only under that spelling.</summary>
	[Test]
	public void A_nested_type_is_found_under_its_metadata_spelling()
	{
		using var declaring = CompiledModule.Of(ProductCore, "Pc.Core");

		Assert.Single(TypeOwners.Of([declaring.ModulePath], "Product.Core.Widget+Part", assembly: null));
		Assert.Empty(TypeOwners.Of([declaring.ModulePath], "Product.Core.Part", assembly: null));
	}

	/// <summary>
	/// A type two modules declare is reported with both, and the sentence gives the spelling that picks
	/// one, because choosing would bind a breakpoint that may never fire.
	/// </summary>
	[Test]
	public void A_type_two_modules_declare_is_reported_with_both_and_the_spelling_that_chooses()
	{
		using var first = CompiledModule.Of(ProductCore, "Pc.Core");
		using var second = CompiledModule.Of(ProductCore, "Pc.Legacy");

		var owners = TypeOwners.Of([first.ModulePath, second.ModulePath], "Product.Core.Widget", assembly: null);
		Assert.Equal(new[] { first.ModulePath, second.ModulePath }, owners);

		var sentence = TypeOwners.Ambiguity("Product.Core.Widget", owners, "Product.Core.Widget.Refresh");
		Assert.Contains("Pc.Core.dll, Pc.Legacy.dll", sentence);
		Assert.Contains("Pc.Core!Product.Core.Widget.Refresh", sentence);
	}

	/// <summary>A stated assembly keeps only modules of that name, matched without regard to case.</summary>
	[Test]
	public void A_stated_assembly_keeps_only_the_module_of_that_name()
	{
		using var first = CompiledModule.Of(ProductCore, "Pc.Core");
		using var second = CompiledModule.Of(ProductCore, "Pc.Legacy");

		var owners = TypeOwners.Of([first.ModulePath, second.ModulePath], "Product.Core.Widget", assembly: "pc.legacy");

		Assert.Equal(new[] { second.ModulePath }, owners);
	}

	/// <summary>
	/// A module file that cannot be read declares nothing rather than failing the search: a target
	/// carries modules of every kind, and one odd one must not stop a breakpoint binding in the rest.
	/// </summary>
	[Test]
	public void A_module_that_cannot_be_read_declares_nothing()
	{
		using var declaring = CompiledModule.Of(ProductCore, "Pc.Core");
		var missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.dll");

		var owners = TypeOwners.Of([missing, declaring.ModulePath], "Product.Core.Widget", assembly: null);

		Assert.Equal(new[] { declaring.ModulePath }, owners);
	}
}
