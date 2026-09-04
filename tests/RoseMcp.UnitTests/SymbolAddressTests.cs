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
}
