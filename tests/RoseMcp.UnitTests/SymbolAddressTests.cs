using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.UnitTests;

/// <summary>
/// Taking a written name apart. The point of addressing code by name is that a caller can write as
/// much of it as it happens to know, so what these check is that the shortest useful spelling and
/// the longest both survive the trip -- and that a parameter list is separated from the name rather
/// than becoming part of it.
/// </summary>
public sealed class SymbolAddressTests
{
	[Test]
	public void Takes_the_last_segment_as_the_name()
	{
		var address = SymbolAddress.Parse("RoseMcp.Broker.LiveAppSession.ReadEventsAsync");

		address.Name.ShouldBe("ReadEventsAsync");
		address.Path.ShouldBe(["RoseMcp", "Broker", "LiveAppSession", "ReadEventsAsync"]);
		address.Parameters.ShouldBeNull();
	}

	[Test]
	public void Accepts_a_bare_name()
	{
		var address = SymbolAddress.Parse("ReadEventsAsync");

		address.Name.ShouldBe("ReadEventsAsync");
		address.Path.ShouldBe(["ReadEventsAsync"]);
	}

	/// <summary>
	/// Putting one together, which is the direction a result needs. What was reported before was the
	/// reading format -- return type first, parameters named -- and none of it parsed back.
	/// </summary>
	[Test]
	[Arguments("Shop.Till", "Shop.Till")]
	[Arguments("Shop.Till.Total", "Shop.Till.Total")]
	[Arguments("Shop.Till.Ring", "Shop.Till.Ring(string, int)")]
	[Arguments("Shop.Till.Wrap", "Shop.Till.Wrap()")]
	[Arguments("Shop.Till.Detach", "Shop.Till.Detach(out string, ref int, in int, params int[])")]
	public void Spells_an_address_a_caller_can_write(string name, string expected) =>
		SymbolAddress.Of(Symbol(name)).ShouldBe(expected);

	/// <summary>
	/// And it goes back in: the address this spells is one <see cref="SymbolAddress.Parse"/> reads and
	/// matches to the symbol it came from. Asserting the round trip rather than the string is what
	/// makes this a contract instead of a snapshot of a display format.
	/// </summary>
	[Test]
	[Arguments("Shop.Till.Ring")]
	[Arguments("Shop.Till.Wrap")]
	[Arguments("Shop.Till.Total")]
	// A generic parameter type, because dropping its type arguments spells an address naming a type
	// that does not exist: ReadOnlyMemory is not ReadOnlyMemory<char>, and the match knows it.
	[Arguments("Shop.Till.Scan")]
	[Arguments("Shop.Till.Detach")]
	public void Reads_back_the_address_it_spelled(string name)
	{
		var symbol = Symbol(name);
		var address = SymbolAddress.Of(symbol);

		address.ShouldNotBeNull();
		SymbolAddress.Parse(address).Matches(symbol).ShouldBeTrue($"'{address}' does not match the symbol it came from");
	}

	/// <summary>
	/// How a parameter is passed is part of which overload it is, so a parameter written without
	/// <c>out</c> is one passed by value and does not reach one passed out. Two overloads differing
	/// by nothing else could otherwise not be told apart by any spelling of the first.
	/// </summary>
	[Test]
	[Arguments("Shop.Till.Detach(out string, ref int, in int, int[])", true)]
	[Arguments("Shop.Till.Detach(out string, ref int, in int, params int[])", true)]
	[Arguments("Shop.Till.Detach(string, int, int, int[])", false)]
	[Arguments("Shop.Till.Detach(ref string, ref int, in int, int[])", false)]
	public void Tells_how_a_parameter_is_passed_apart(string requested, bool matches) =>
		SymbolAddress.Parse(requested).Matches(Symbol("Shop.Till.Detach")).ShouldBe(matches);

	/// <summary>
	/// A parameter type is named by the language's keyword or by the framework's type, qualified or not,
	/// since a signature read out of one tool may spell it either way. The framework's qualified name is
	/// the one a caller copying from documentation or a stack trace writes.
	/// </summary>
	[Test]
	[Arguments("Shop.Till.Ring(string, int)")]
	[Arguments("Shop.Till.Ring(String, Int32)")]
	[Arguments("Shop.Till.Ring(System.String, System.Int32)")]
	[Arguments("Shop.Till.Ring(global::System.String, int)")]
	public void Matches_a_parameter_type_by_any_of_its_names(string requested) =>
		SymbolAddress.Parse(requested).Matches(Symbol("Shop.Till.Ring")).ShouldBeTrue();

	/// <summary>
	/// A parameter has no address, and says so rather than reporting a bare identifier that would
	/// invite a call that cannot work: it is declared inside a member rather than as one, so no
	/// declaration search could find it.
	/// </summary>
	[Test]
	public void Spells_no_address_for_something_nothing_can_name()
	{
		var method = (IMethodSymbol)Symbol("Shop.Till.Ring");

		SymbolAddress.Of(method.Parameters[0]).ShouldBeNull();
	}

	/// <summary>
	/// One compilation, from source, so these assert what the formatter does rather than what a
	/// solution happens to hold. Two overloads because separating them is most of what an address is
	/// for.
	/// </summary>
	private static ISymbol Symbol(string name)
	{
		const string Source = """
			namespace Shop;
	
			public class Till
			{
				public int Total { get; set; }
	
				public string Ring(string item, int pence) => item;
	
				public string Wrap() => "wrapped";

				public string Scan(System.ReadOnlyMemory<char> code) => "";

			public bool Detach(out string failure, ref int count, in int limit, params int[] rest)
			{
				failure = "";
				return true;
			}
			}
			""";

		var compilation = CSharpCompilation.Create(
			"Addresses",
			[CSharpSyntaxTree.ParseText(Source)],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

		var found = compilation.GetSymbolsWithName(
			candidate => candidate == name.Split('.')[^1],
			SymbolFilter.TypeAndMember).ToArray();

		found.ShouldHaveSingleItem();

		return found[0];
	}

	/// <summary>
	/// Type arguments are dropped, so a caller does not have to know how the declaration spells its
	/// type parameters to name a member of it.
	/// </summary>
	[Test]
	[Arguments("Cache<string>.Add")]
	[Arguments("Cache<TKey, TValue>.Add")]
	[Arguments("Outer<T>.Inner<U>.Add")]
	public void Ignores_type_arguments(string requested)
	{
		var address = SymbolAddress.Parse(requested);

		address.Name.ShouldBe("Add");
		string.Join(".", address.Path).ShouldNotContain("<", Case.Sensitive);
	}

	[Test]
	public void Separates_a_parameter_list_from_the_name()
	{
		var address = SymbolAddress.Parse("Log.Write(string, int)");

		address.Name.ShouldBe("Write");
		address.Path.ShouldBe(["Log", "Write"]);
		address.Parameters.ShouldBe(["string", "int"]);
	}

	/// <summary>
	/// A generic parameter carries commas of its own, and splitting on those would turn one
	/// parameter into two and match nothing.
	/// </summary>
	[Test]
	public void Splits_only_the_commas_that_separate_parameters()
	{
		var address = SymbolAddress.Parse("Log.Write(Func<int, string>, IReadOnlyList<int[]>)");

		address.Parameters.ShouldBe(["Func<int, string>", "IReadOnlyList<int[]>"]);
	}

	/// <summary>
	/// Empty parentheses are a constraint and their absence is not: one asks for the overload taking
	/// nothing, the other asks for whichever there is.
	/// </summary>
	[Test]
	public void Tells_no_parameters_apart_from_no_parameter_list()
	{
		SymbolAddress.Parse("Session.Close()").Parameters!.ShouldBeEmpty();
		SymbolAddress.Parse("Session.Close").Parameters.ShouldBeNull();
	}

	[Test]
	public void Drops_a_global_alias()
	{
		SymbolAddress.Parse("global::RoseMcp.Worker.Whitespace").Path.ShouldBe(["RoseMcp", "Worker", "Whitespace"]);
	}

	[Test]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments(".")]
	public void Refuses_a_name_that_names_nothing(string requested)
	{
		Should.Throw<ArgumentException>(() => SymbolAddress.Parse(requested)).ShouldBeOfType<ArgumentException>();
	}

	[Test]
	public void Refuses_a_parameter_list_that_was_never_opened()
	{
		Should.Throw<ArgumentException>(() => SymbolAddress.Parse("Log.Write string)")).ShouldBeOfType<ArgumentException>();
	}

	/// <summary>
	/// Both constructor spellings, and both leaving the address pointing at the type, since that is
	/// the name the constructor is declared under.
	/// </summary>
	[Test]
	[Arguments("RoseMcp.Worker.Whitespace.Whitespace")]
	[Arguments("RoseMcp.Worker.Whitespace..ctor")]
	public void Reads_a_constructor_as_its_type(string requested)
	{
		var address = SymbolAddress.Parse(requested);

		address.Constructor.ShouldBe(ConstructorKind.Instance);
		address.Name.ShouldBe("Whitespace");
		address.Path.ShouldBe(["RoseMcp", "Worker", "Whitespace"]);
	}

	/// <summary>
	/// A repeated last segment is a constructor as C# spells one, and equally a type named for the
	/// namespace it is in. Both readings are kept, the type first, for the compilation to settle.
	/// </summary>
	[Test]
	public void Keeps_both_readings_of_a_repeated_last_segment()
	{
		var readings = SymbolAddress.Parse("RoseMcp.XamlDiff.XamlDiff").Readings;

		readings.Count.ShouldBe(2);
		readings[0].Constructor.ShouldBe(ConstructorKind.None);
		readings[0].Path.ShouldBe(["RoseMcp", "XamlDiff", "XamlDiff"]);
		readings[1].Constructor.ShouldBe(ConstructorKind.Instance);
		readings[1].Path.ShouldBe(["RoseMcp", "XamlDiff"]);
	}

	/// <summary>
	/// The spellings only a constructor fits are read one way: a type takes no parameter list, and
	/// <c>..ctor</c> is the CLR's name for a constructor and nothing else.
	/// </summary>
	[Test]
	[Arguments("Shop.Gadget.Gadget(int)")]
	[Arguments("Shop.Gadget..ctor")]
	[Arguments("Shop.Gadget.Ring")]
	public void Reads_a_spelling_only_one_reading_fits_one_way(string requested) =>
		SymbolAddress.Parse(requested).Readings.Count.ShouldBe(1);

	[Test]
	public void Reads_a_static_constructor()
	{
		var address = SymbolAddress.Parse("RoseMcp.Worker.Whitespace..cctor");

		address.Constructor.ShouldBe(ConstructorKind.Static);
		address.Name.ShouldBe("Whitespace");
	}

	/// <summary>
	/// A parameter list picks the overload, and separating it happens before the constructor
	/// spelling is read, so both halves of Type.Type(int) survive.
	/// </summary>
	[Test]
	public void Keeps_the_parameter_list_of_a_constructor()
	{
		var address = SymbolAddress.Parse("LiveAppSessionTests.LiveAppSessionTests(UwpProbeApp, WinUiProbeApp)");

		address.Constructor.ShouldBe(ConstructorKind.Instance);
		address.Name.ShouldBe("LiveAppSessionTests");
		address.Parameters.ShouldBe(["UwpProbeApp", "WinUiProbeApp"]);
	}

	/// <summary>
	/// An ordinary member is not read as a constructor, however its segments repeat elsewhere in the
	/// path. Only the last two matching means one, because C# forbids a member sharing the name of
	/// the type enclosing it.
	/// </summary>
	[Test]
	[Arguments("RoseMcp.Worker.Whitespace.Shift")]
	[Arguments("Whitespace.Whitespace.Shift")]
	public void Leaves_an_ordinary_member_alone(string requested)
	{
		SymbolAddress.Parse(requested).Constructor.ShouldBe(ConstructorKind.None);
	}

	[Test]
	public void Refuses_a_constructor_with_no_type()
	{
		Should.Throw<ArgumentException>(() => SymbolAddress.Parse("..ctor")).ShouldBeOfType<ArgumentException>();
	}
}
