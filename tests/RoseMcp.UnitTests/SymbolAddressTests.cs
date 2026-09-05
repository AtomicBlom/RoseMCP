namespace RoseMcp.UnitTests;

/// <summary>
/// Taking a written name apart. The point of addressing code by name is that a caller can write as
/// much of it as it happens to know, so what these check is that the shortest useful spelling and
/// the longest both survive the trip -- and that a parameter list is separated from the name rather
/// than becoming part of it.
/// </summary>
public sealed class SymbolAddressTests
{
	[Fact]
	public void Takes_the_last_segment_as_the_name()
	{
		var address = SymbolAddress.Parse("RoseMcp.Broker.LiveAppSession.ReadEventsAsync");

		Assert.Equal("ReadEventsAsync", address.Name);
		Assert.Equal(["RoseMcp", "Broker", "LiveAppSession", "ReadEventsAsync"], address.Path);
		Assert.Null(address.Parameters);
	}

	[Fact]
	public void Accepts_a_bare_name()
	{
		var address = SymbolAddress.Parse("ReadEventsAsync");

		Assert.Equal("ReadEventsAsync", address.Name);
		Assert.Equal(["ReadEventsAsync"], address.Path);
	}

	/// <summary>
	/// Type arguments are dropped, so a caller does not have to know how the declaration spells its
	/// type parameters to name a member of it.
	/// </summary>
	[Theory]
	[InlineData("Cache<string>.Add")]
	[InlineData("Cache<TKey, TValue>.Add")]
	[InlineData("Outer<T>.Inner<U>.Add")]
	public void Ignores_type_arguments(string requested)
	{
		var address = SymbolAddress.Parse(requested);

		Assert.Equal("Add", address.Name);
		Assert.DoesNotContain("<", string.Join(".", address.Path), StringComparison.Ordinal);
	}

	[Fact]
	public void Separates_a_parameter_list_from_the_name()
	{
		var address = SymbolAddress.Parse("Log.Write(string, int)");

		Assert.Equal("Write", address.Name);
		Assert.Equal(["Log", "Write"], address.Path);
		Assert.Equal(["string", "int"], address.Parameters);
	}

	/// <summary>
	/// A generic parameter carries commas of its own, and splitting on those would turn one
	/// parameter into two and match nothing.
	/// </summary>
	[Fact]
	public void Splits_only_the_commas_that_separate_parameters()
	{
		var address = SymbolAddress.Parse("Log.Write(Func<int, string>, IReadOnlyList<int[]>)");

		Assert.Equal(["Func<int, string>", "IReadOnlyList<int[]>"], address.Parameters);
	}

	/// <summary>
	/// Empty parentheses are a constraint and their absence is not: one asks for the overload taking
	/// nothing, the other asks for whichever there is.
	/// </summary>
	[Fact]
	public void Tells_no_parameters_apart_from_no_parameter_list()
	{
		Assert.Empty(SymbolAddress.Parse("Session.Close()").Parameters!);
		Assert.Null(SymbolAddress.Parse("Session.Close").Parameters);
	}

	[Fact]
	public void Drops_a_global_alias()
	{
		Assert.Equal(["RoseMcp", "Worker", "Whitespace"], SymbolAddress.Parse("global::RoseMcp.Worker.Whitespace").Path);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(".")]
	public void Refuses_a_name_that_names_nothing(string requested)
	{
		Assert.Throws<ArgumentException>(() => SymbolAddress.Parse(requested));
	}

	[Fact]
	public void Refuses_a_parameter_list_that_was_never_opened()
	{
		Assert.Throws<ArgumentException>(() => SymbolAddress.Parse("Log.Write string)"));
	}

	/// <summary>
	/// Both constructor spellings, and both leaving the address pointing at the type, since that is
	/// the name the constructor is declared under.
	/// </summary>
	[Theory]
	[InlineData("RoseMcp.Worker.Whitespace.Whitespace")]
	[InlineData("RoseMcp.Worker.Whitespace..ctor")]
	public void Reads_a_constructor_as_its_type(string requested)
	{
		var address = SymbolAddress.Parse(requested);

		Assert.Equal(ConstructorKind.Instance, address.Constructor);
		Assert.Equal("Whitespace", address.Name);
		Assert.Equal(["RoseMcp", "Worker", "Whitespace"], address.Path);
	}

	[Fact]
	public void Reads_a_static_constructor()
	{
		var address = SymbolAddress.Parse("RoseMcp.Worker.Whitespace..cctor");

		Assert.Equal(ConstructorKind.Static, address.Constructor);
		Assert.Equal("Whitespace", address.Name);
	}

	/// <summary>
	/// A parameter list picks the overload, and separating it happens before the constructor
	/// spelling is read, so both halves of Type.Type(int) survive.
	/// </summary>
	[Fact]
	public void Keeps_the_parameter_list_of_a_constructor()
	{
		var address = SymbolAddress.Parse("LiveAppSessionTests.LiveAppSessionTests(UwpProbeApp, WinUiProbeApp)");

		Assert.Equal(ConstructorKind.Instance, address.Constructor);
		Assert.Equal("LiveAppSessionTests", address.Name);
		Assert.Equal(["UwpProbeApp", "WinUiProbeApp"], address.Parameters);
	}

	/// <summary>
	/// An ordinary member is not read as a constructor, however its segments repeat elsewhere in the
	/// path. Only the last two matching means one, because C# forbids a member sharing the name of
	/// the type enclosing it.
	/// </summary>
	[Theory]
	[InlineData("RoseMcp.Worker.Whitespace.Shift")]
	[InlineData("Whitespace.Whitespace.Shift")]
	public void Leaves_an_ordinary_member_alone(string requested)
	{
		Assert.Equal(ConstructorKind.None, SymbolAddress.Parse(requested).Constructor);
	}

	[Fact]
	public void Refuses_a_constructor_with_no_type()
	{
		Assert.Throws<ArgumentException>(() => SymbolAddress.Parse("..ctor"));
	}
}
