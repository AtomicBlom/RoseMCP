using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The grammar that addresses a value inside a stopped frame.
/// <para>
/// Worth testing rather than eyeballing because both ends of it have to agree exactly: the debugger
/// writes a path onto every variable it reports and reads the same path back to expand one. A
/// grammar that parses <c>arg:0.Inner[2]</c> as something other than what it says would expand the
/// wrong value and report success, which is the failure with no symptom.
/// </para>
/// </summary>
public sealed class ValuePathTests
{
	[Test]
	[Arguments("arg:0", ValuePathRoot.Argument, 0)]
	[Arguments("arg:12", ValuePathRoot.Argument, 12)]
	[Arguments("local:0", ValuePathRoot.Local, 0)]
	[Arguments("local:7", ValuePathRoot.Local, 7)]
	public void A_slot_root_carries_its_kind_and_number(string path, ValuePathRoot kind, int slot)
	{
		var parsed = ValuePath.Parse(path);

		Assert.Equal(kind, parsed.Kind);
		Assert.Equal(slot, parsed.Slot);
		Assert.Empty(parsed.Steps);
	}

	/// <summary>
	/// A bare name is the form a person types and the form an evaluation has always taken, so it
	/// stays a root rather than being refused in favour of a slot nobody can see.
	/// </summary>
	[Test]
	public void A_bare_name_is_a_root()
	{
		var parsed = ValuePath.Parse("state");

		Assert.Equal(ValuePathRoot.Name, parsed.Kind);
		Assert.Equal("state", parsed.Name);

		// "this" is a name like any other: it is what an instance method's argument 0 is called.
		Assert.Equal("this", ValuePath.Parse("this").Name);
	}

	[Test]
	public void Fields_and_indexes_step_in_the_order_they_are_written()
	{
		var parsed = ValuePath.Parse("arg:0.Items[3].Name");

		Assert.Equal(ValuePathRoot.Argument, parsed.Kind);
		Assert.Equal(0, parsed.Slot);
		Assert.Equal(3, parsed.Steps.Count);
		Assert.Equal("Items", parsed.Steps[0].Field);
		Assert.Equal(3, parsed.Steps[1].Index);
		Assert.Equal("Name", parsed.Steps[2].Field);
	}

	/// <summary>An index straight off the root, with no field between, is the ordinary array case.</summary>
	[Test]
	public void A_root_can_be_indexed_directly()
	{
		var parsed = ValuePath.Parse("local:1[0]");

		Assert.Equal(ValuePathRoot.Local, parsed.Kind);
		Assert.Equal(1, parsed.Slot);
		Assert.Equal(0, Assert.Single(parsed.Steps).Index);
	}

	[Test]
	public void Surrounding_space_is_not_part_of_the_path()
	{
		Assert.Equal(2, ValuePath.Parse("  arg:2  ").Slot);
	}

	/// <summary>
	/// Each of these could be read as something plausible, and every plausible reading addresses a
	/// value the caller did not name. So each is refused with what failed, rather than guessed at.
	/// </summary>
	[Test]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments("arg:")]
	[Arguments("arg:x")]
	[Arguments("arg:-1")]
	[Arguments("local:")]
	[Arguments("what:0")]
	[Arguments("arg:0.")]
	[Arguments("arg:0[")]
	[Arguments("arg:0[2")]
	[Arguments("arg:0[x]")]
	[Arguments("arg:0[-1]")]
	[Arguments("arg:0]")]
	public void A_path_that_cannot_be_read_is_refused(string path)
	{
		var refusal = Assert.Throws<ArgumentException>(() => ValuePath.Parse(path));

		Assert.NotEmpty(refusal.Message);
	}

	/// <summary>
	/// Composing and parsing are one grammar, so a path the debugger writes onto a variable is one
	/// it can read back. That round trip is the whole contract between the two ends.
	/// </summary>
	[Test]
	public void What_is_composed_parses_back_to_what_composed_it()
	{
		var path = ValuePath.Element(ValuePath.Field(ValuePath.Argument(1), "Inner"), 4);

		Assert.Equal("arg:1.Inner[4]", path);

		var parsed = ValuePath.Parse(path);
		Assert.Equal(ValuePathRoot.Argument, parsed.Kind);
		Assert.Equal(1, parsed.Slot);
		Assert.Equal("Inner", parsed.Steps[0].Field);
		Assert.Equal(4, parsed.Steps[1].Index);
	}

	/// <summary>
	/// A local whose name the PDB does not give is addressed by slot, and that address parses. It is
	/// the case the slot forms exist for: a compiler temporary has no name to pass back.
	/// </summary>
	[Test]
	public void A_slot_addresses_a_local_no_name_reaches()
	{
		Assert.Equal("local:3", ValuePath.Local(3));
		Assert.Equal(3, ValuePath.Parse(ValuePath.Local(3)).Slot);
	}
}
