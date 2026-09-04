namespace RoseMcp.UnitTests;

/// <summary>
/// Reading a name the way the code spells it. Everything else about resolving a name needs a
/// compilation; this part has rules of its own, and both of them decide what gets searched for.
/// </summary>
public sealed class NameResolverTests
{
	[Fact]
	public void Takes_a_bare_name_as_it_stands()
	{
		var (name, arity) = NameResolver.Parse("Encoding");

		Assert.Equal("Encoding", name);
		Assert.Null(arity);
	}

	/// <summary>
	/// The first segment, not the last: what failed to resolve in <c>Encoding.UTF8</c> is
	/// <c>Encoding</c>, and searching for <c>UTF8</c> would find nothing and say so confidently.
	/// </summary>
	[Theory]
	[InlineData("Encoding.UTF8", "Encoding")]
	[InlineData("Path.Combine", "Path")]
	public void Takes_the_first_segment_of_a_dotted_name(string supplied, string expected) =>
		Assert.Equal(expected, NameResolver.Parse(supplied).Name);

	/// <summary>
	/// Counted at the top level only, so a type argument that is itself generic does not inflate
	/// the count and rule out the type that would have resolved.
	/// </summary>
	[Theory]
	[InlineData("List<int>", "List", 1)]
	[InlineData("Dictionary<string, int>", "Dictionary", 2)]
	[InlineData("Dictionary<string, List<int>>", "Dictionary", 2)]
	[InlineData("Func<Dictionary<int, string>, Task<bool>, int>", "Func", 3)]
	public void Reads_the_arity_off_the_type_arguments(string supplied, string expected, int arity)
	{
		var parsed = NameResolver.Parse(supplied);

		Assert.Equal(expected, parsed.Name);
		Assert.Equal(arity, parsed.Arity);
	}

	/// <summary>
	/// An unbound name is the same arity as a bound one. <c>List&lt;&gt;</c> is the open form of a
	/// type taking one argument, not of a type taking none, and counting nothing there would send
	/// the search looking for a non-generic type that does not exist.
	/// </summary>
	[Theory]
	[InlineData("List<>", 1)]
	[InlineData("Dictionary<,>", 2)]
	public void Counts_an_unbound_generic_by_its_commas(string supplied, int arity) =>
		Assert.Equal(arity, NameResolver.Parse(supplied).Arity);

	/// <summary>
	/// A caller that says how the name is used outranks the spelling, because the spelling is
	/// whatever they happened to paste and the arity is something they had to mean.
	/// </summary>
	[Fact]
	public void Keeps_an_arity_the_caller_supplied()
	{
		Assert.Equal(3, NameResolver.Parse("List<int>", 3).Arity);
		Assert.Equal(2, NameResolver.Parse("Palette", 2).Arity);
	}

	/// <summary>
	/// Both at once, and in the right order. The dot in <c>List&lt;Foo.Bar&gt;</c> is inside the type
	/// arguments, so splitting on the first dot before taking the arguments off would search for
	/// <c>List&lt;Foo</c> -- a name nothing is called, reported as confidently as any other.
	/// </summary>
	[Theory]
	[InlineData("  ImmutableArray<int>.Empty  ", "ImmutableArray", 1)]
	[InlineData("List<Foo.Bar>", "List", 1)]
	[InlineData("Dictionary<string, Foo.Bar>.Entry", "Dictionary", 2)]
	public void Handles_type_arguments_and_qualification_together(string supplied, string expected, int arity)
	{
		var (name, parsed) = NameResolver.Parse(supplied);

		Assert.Equal(expected, name);
		Assert.Equal(arity, parsed);
	}
}
