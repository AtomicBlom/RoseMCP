using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Naming an enum value read off a target from its module's metadata, which is how the debugger
/// shows <c>Rectangle</c> rather than <c>{Shapes.Kind}</c> without calling the debuggee's ToString.
/// </summary>
public sealed class EnumShapeTests
{
	private const string Source = """
		namespace Shapes;

		public enum Kind { None, Rectangle, Ellipse, Ink = 10 }

		[System.Flags]
		public enum Style { None = 0, Bold = 1, Italic = 2, Underline = 4, Emphasis = Bold | Italic }

		public enum Direction : sbyte { Back = -1, Still = 0, Forward = 1 }

		public enum Wide : ulong { Top = ulong.MaxValue }

		public struct Dimensions { public int Width; public int Height; }
		""";

	[Test]
	public void A_value_is_named_by_the_member_it_equals()
	{
		using var module = CompiledModule.Of(Source, "Shapes");
		var kind = EnumShape.Of(module.ModulePath, TokenOf(module, "Shapes.Kind")).ShouldNotBeNull();

		kind.IsFlags.ShouldBeFalse();
		kind.Name(1).ShouldBe("Rectangle");
		kind.Name(10).ShouldBe("Ink");
	}

	/// <summary>
	/// Any integer fits in an enum, so a value no member names is a real state rather than a failed
	/// read; it comes back unnamed, and the caller shows the number.
	/// </summary>
	[Test]
	public void A_value_no_member_names_is_left_for_the_number()
	{
		using var module = CompiledModule.Of(Source, "Shapes");
		var kind = EnumShape.Of(module.ModulePath, TokenOf(module, "Shapes.Kind")).ShouldNotBeNull();

		kind.Name(7).ShouldBeNull();
	}

	/// <summary>
	/// Flags are named by the members they are made of, smallest first, preferring a member that
	/// names several bits at once -- the way Enum.ToString prints them -- and left unnamed when a bit
	/// belongs to no member, since naming the rest would hide it.
	/// </summary>
	[Test]
	public void Flags_are_named_by_the_members_they_combine()
	{
		using var module = CompiledModule.Of(Source, "Shapes");
		var style = EnumShape.Of(module.ModulePath, TokenOf(module, "Shapes.Style")).ShouldNotBeNull();

		style.IsFlags.ShouldBeTrue();
		style.Name(0).ShouldBe("None");
		style.Name(4).ShouldBe("Underline");
		style.Name(3).ShouldBe("Emphasis");
		style.Name(5).ShouldBe("Bold | Underline");
		style.Name(7).ShouldBe("Emphasis | Underline");
		style.Name(8).ShouldBeNull();
	}

	/// <summary>
	/// A negative member is sign-extended, and a value read off the target is widened the same way, so
	/// -1 matches whatever width the enum is declared at. The widest unsigned one keeps every bit.
	/// </summary>
	[Test]
	public void Signed_and_wide_members_compare_after_widening()
	{
		using var module = CompiledModule.Of(Source, "Shapes");

		var direction = EnumShape.Of(module.ModulePath, TokenOf(module, "Shapes.Direction")).ShouldNotBeNull();
		direction.Name(unchecked((ulong)-1L)).ShouldBe("Back");

		var wide = EnumShape.Of(module.ModulePath, TokenOf(module, "Shapes.Wide")).ShouldNotBeNull();
		wide.Name(ulong.MaxValue).ShouldBe("Top");
	}

	[Test]
	public void A_struct_is_not_an_enum()
	{
		using var module = CompiledModule.Of(Source, "Shapes");

		EnumShape.Of(module.ModulePath, TokenOf(module, "Shapes.Dimensions")).ShouldBeNull();
	}

	private static int TokenOf(CompiledModule module, string fullName)
	{
		using var stream = File.OpenRead(module.ModulePath);
		using var pe = new PEReader(stream);
		var metadata = pe.GetMetadataReader();

		foreach (var handle in metadata.TypeDefinitions)
		{
			var type = metadata.GetTypeDefinition(handle);
			var name = $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
			if (name == fullName) return MetadataTokens.GetToken(handle);
		}

		throw new InvalidOperationException($"{fullName} is not in the compiled module.");
	}
}
