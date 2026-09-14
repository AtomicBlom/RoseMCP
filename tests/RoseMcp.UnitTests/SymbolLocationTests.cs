using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The grammar a breakpoint's location is written in.
/// <para>
/// Everything it can get wrong is a breakpoint somewhere other than where it was asked for,
/// reported as a success -- the module guessed from the wrong segment, a suffix mistaken for part of
/// a name, an offset quietly dropped. None of those looks like a failure from the outside, which is
/// why each of them is arranged here deliberately.
/// </para>
/// </summary>
public sealed class SymbolLocationTests
{
	/// <summary>
	/// The bare form names no assembly -- which module declares the type is found when the location is
	/// bound, never guessed from the name -- and the explicit form states one.
	/// </summary>
	[Test]
	public void A_bare_name_names_no_assembly_and_a_prefix_states_one()
	{
		var bare = SymbolLocation.Parse("MyApp.Ui.Widget.Refresh");
		Assert.Null(bare.Assembly);
		Assert.Equal("MyApp.Ui.Widget", bare.TypeName);
		Assert.Equal("Refresh", bare.MethodName);
		Assert.Null(bare.IlOffset);

		var stated = SymbolLocation.Parse("Widgets!MyApp.Ui.Widget.Refresh");
		Assert.Equal("Widgets", stated.Assembly);
		Assert.Equal("MyApp.Ui.Widget", stated.TypeName);
	}

	/// <summary>
	/// An assembly written as a file name is taken down to the simple name a module is matched against,
	/// so every spelling of it chooses the same module.
	/// </summary>
	[Test]
	[Arguments("Widgets.dll!MyApp.Widget.Refresh")]
	[Arguments("Widgets.exe!MyApp.Widget.Refresh")]
	[Arguments("Widgets!MyApp.Widget.Refresh")]
	public void An_assembly_file_name_is_taken_down_to_its_simple_name(string spec)
	{
		Assert.Equal("Widgets", SymbolLocation.Parse(spec).Assembly);
	}

	/// <summary>A dotted assembly name must not have its last segment mistaken for an extension.</summary>
	[Test]
	public void A_dotted_assembly_name_keeps_all_of_itself()
	{
		Assert.Equal("MyApp.Core", SymbolLocation.Parse("MyApp.Core!MyApp.Widget.Refresh").Assembly);
	}

	[Test]
	public void An_instruction_offset_is_read_off_the_end()
	{
		var picked = SymbolLocation.Parse("Widgets!MyApp.Widget.Refresh@IL_001f");

		Assert.Equal(0x1f, picked.IlOffset);
		Assert.Equal("MyApp.Widget", picked.TypeName);
		Assert.Equal("Refresh", picked.MethodName);
		Assert.Equal("Widgets", picked.Assembly);
	}

	/// <summary>
	/// The spellings a position picker actually produces. A breakpoint inside a lambda binds in a
	/// closure type and one after an await in a state machine, and both names are full of the
	/// punctuation this grammar uses for something else.
	/// </summary>
	[Test]
	[Arguments("Widgets!MyApp.Widget+<>c.<Refresh>b__3_0@IL_0007", "MyApp.Widget+<>c", "<Refresh>b__3_0", 7)]
	[Arguments("Widgets!MyApp.Widget+<Refresh>d__3.MoveNext@IL_0042", "MyApp.Widget+<Refresh>d__3", "MoveNext", 0x42)]
	[Arguments("Widgets!MyApp.Widget.<Refresh>g__Inner|3_0@IL_0000", "MyApp.Widget", "<Refresh>g__Inner|3_0", 0)]
	public void A_compiler_generated_method_survives_the_grammar(
		string spec,
		string typeName,
		string methodName,
		int ilOffset)
	{
		var parsed = SymbolLocation.Parse(spec);

		Assert.Equal(typeName, parsed.TypeName);
		Assert.Equal(methodName, parsed.MethodName);
		Assert.Equal(ilOffset, parsed.IlOffset);
	}

	/// <summary>
	/// The marker is anchored past the last dot, so something spelled like it inside a type name is
	/// part of the name. Reading it as an offset would take the method name away with it.
	/// </summary>
	[Test]
	public void A_marker_inside_a_type_name_is_part_of_the_name()
	{
		var parsed = SymbolLocation.Parse("Widgets!MyApp.Widget@IL_0001.Refresh");

		Assert.Equal("MyApp.Widget@IL_0001", parsed.TypeName);
		Assert.Equal("Refresh", parsed.MethodName);
		Assert.Null(parsed.IlOffset);
	}

	/// <summary>
	/// A marker that is there and unreadable is refused. Falling back to the method's first
	/// instruction would stop the target somewhere other than where somebody pointed and call it a
	/// success, and nothing downstream could tell the difference.
	/// </summary>
	[Test]
	[Arguments("Widgets!MyApp.Widget.Refresh@IL_")]
	[Arguments("Widgets!MyApp.Widget.Refresh@IL_zz")]
	[Arguments("Widgets!MyApp.Widget.Refresh@IL_ 1")]
	public void An_offset_that_does_not_read_is_refused(string spec)
	{
		Assert.Throws<ArgumentException>(() => SymbolLocation.Parse(spec));
	}

	/// <summary>
	/// An offset with the high bit set reads as a negative number, which is not an instruction in any
	/// method. Left in, it would reach the runtime as a breakpoint request nobody could explain.
	/// </summary>
	[Test]
	public void An_offset_that_is_not_a_position_is_refused()
	{
		Assert.Throws<ArgumentException>(() => SymbolLocation.Parse("Widgets!MyApp.Widget.Refresh@IL_ffffffff"));
	}

	[Test]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments("Refresh")]
	[Arguments("MyApp.Widget.")]
	public void Something_that_is_not_a_location_is_refused(string spec)
	{
		Assert.Throws<ArgumentException>(() => SymbolLocation.Parse(spec));
	}
}
