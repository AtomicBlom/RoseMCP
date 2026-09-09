namespace RoseMcp.UnitTests;

/// <summary>
/// Every shape a real call site takes, one case each, against the rewriter that has to put its
/// arguments back.
/// <para>
/// Enumerated rather than sampled, because the failure this code produces has no symptom worth
/// trusting. An argument bound to the wrong parameter compiles about as often as not, and when it
/// does not, the diagnostic names the caller's file rather than the tool that wrote it -- so the
/// shapes that are handled and the shapes that are not can only be told apart by asking each one.
/// </para>
/// <para>
/// Three of them were not handled when this was written, and each case says what it produced
/// instead: an argument on the wrong parameter where a named one came before a positional one, and
/// the whitespace in front of an argument lost or stranded after the colon when the argument's name
/// went on or came off. Keeping that in the case rather than in a commit message is what makes the
/// next one findable, since all three were silent.
/// </para>
/// <para>
/// The call site is bound in a real compilation rather than parsed on its own, because whether the
/// receiver takes an argument slot is a semantic question -- an extension method invoked on its
/// receiver writes one fewer argument than the method has parameters -- and no amount of reading
/// the text answers it.
/// </para>
/// </summary>
public sealed class CallSiteShapeMatrixTests
{
	/// <summary>The method every case changes, so the fixtures can be found without being told.</summary>
	private const string Target = "Target";

	/// <summary>
	/// The change the two-parameter cases make: a parameter inserted between the two that are
	/// already there. Inserting rather than appending is what makes every call site have to move,
	/// which is the point -- a parameter added on the end leaves them all alone.
	/// </summary>
	private const string Inserted = "string first, string separator, string second";

	/// <summary>What to pass for the inserted parameter.</summary>
	private const string Dash = "separator=\"-\"";

	/// <summary>
	/// A call somebody wrapped by hand. Written with its endings spelled out rather than as a
	/// literal, so the case asserts the rewriter's own layout rather than the endings of the file
	/// this test happens to live in.
	/// </summary>
	private const string WrappedCall = "public static class Fixture\n"
		+ "{\n"
		+ "\tpublic static string Target(string first, string second) => first + second;\n"
		+ "\n"
		+ "\tpublic static string Use(string a, string b) => Target(\n"
		+ "\t\ta,\n"
		+ "\t\tb);\n"
		+ "}\n";

	/// <summary>The ordinary shape: every argument in its own place, and the new one lands between them.</summary>
	[Test]
	public void Puts_a_new_argument_between_two_positional_ones()
	{
		Assert.Equal("""(a, "-", b)""", Rewrite(Calling("Target(a, b)"), Inserted, Dash));
	}

	/// <summary>
	/// A call site that names all of its arguments keeps every name that still belongs to the
	/// parameter it named. Keeping one reproduces the text already in the file, so those arguments
	/// come out unchanged; taking it off would be an edit to a site that needed none, and it takes the
	/// only thing saying what a bare literal is for.
	/// <para>
	/// They were stripped, because an argument landing in its own slot was written positionally
	/// whatever it arrived as -- which turned <c>Parse(code, "class", options: null, indent: "\t")</c>
	/// into <c>Parse(code, "class", null, "\t")</c>. The inserted argument is positional because
	/// nothing wrote a name for it.
	/// </para>
	/// </summary>
	[Test]
	public void Keeps_the_names_a_call_site_wrote_for_parameters_it_still_has()
	{
		Assert.Equal(
			"""(first: a, "-", second: b)""",
			Rewrite(Calling("Target(first: a, second: b)"), Inserted, Dash));
	}

	/// <summary>
	/// The mixed shape that works: the named argument is the trailing one. Its name is the caller's
	/// and stays; the positional one beside it had none to keep.
	/// </summary>
	[Test]
	public void Rewrites_a_trailing_named_argument_beside_a_positional_one()
	{
		Assert.Equal("""(a, "-", second: b)""", Rewrite(Calling("Target(a, second: b)"), Inserted, Dash));
	}

	/// <summary>
	/// The mixed shape, and the reason the rest of this matrix exists. A non-trailing named argument
	/// occupies its parameter's position, so the positional argument after it belongs to the next
	/// parameter -- which is what the compiler says and what counting the positional arguments alone
	/// gets wrong.
	/// <para>
	/// Counted, it produced <c>(a, b, separator: "-")</c>: the second argument in the inserted
	/// parameter's place, that parameter named as well so the compiler reports CS1744, and nothing at
	/// all for the parameter with no default -- written to disk, because the rewriter believed it had
	/// succeeded.
	/// </para>
	/// </summary>
	[Test]
	public void Rewrites_a_named_argument_written_before_a_positional_one()
	{
		Assert.Equal("""(first: a, "-", b)""", Rewrite(Calling("Target(first: a, b)"), Inserted, Dash));
	}

	/// <summary>An optional the call site said nothing about goes on saying nothing about it.</summary>
	[Test]
	public void Leaves_an_omitted_optional_omitted()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string first, string second = "x") => first + second;

				public static string Use(string a) => Target(a);
			}
			""";

		Assert.Equal("""(a, "-")""", Rewrite(source, """string first, string separator, string second = "x" """, Dash));
	}

	/// <summary>
	/// A params expansion is several arguments for one parameter, and it has to stay positional --
	/// there is no way to write it as a named argument at all.
	/// </summary>
	[Test]
	public void Keeps_a_params_expansion_together_and_positional()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string first, params string[] rest) => first;

				public static string Use(string a, string b, string c) => Target(a, b, c);
			}
			""";

		Assert.Equal("""(a, "-", b, c)""", Rewrite(source, "string first, string separator, params string[] rest", Dash));
	}

	/// <summary>
	/// The modifiers are the caller's, not the signature's, and they survive because the argument
	/// node is moved rather than regenerated. An <c>out var</c> also declares a variable the rest of
	/// the method uses, so losing it would break code nowhere near the call.
	/// </summary>
	[Test]
	public void Keeps_out_ref_and_in_exactly_as_written()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string first, out int count, ref int value, in int size)
				{
					count = 0;
					return first;
				}

				public static string Use(string a, ref int value, in int size) =>
					Target(a, out var count, ref value, in size);
			}
			""";

		Assert.Equal(
			"""(a, "-", out var count, ref value, in size)""",
			Rewrite(source, "string first, string separator, out int count, ref int value, in int size", Dash));
	}

	/// <summary>
	/// An extension method invoked on its receiver: the first parameter has an argument nowhere in
	/// the list, so every parameter after it is one place to the left of where the text suggests.
	/// </summary>
	[Test]
	public void Counts_the_receiver_of_an_extension_method_as_an_argument_that_is_not_there()
	{
		Assert.Equal(
			"""("-", 4)""",
			Rewrite(Extension("a.Target(4)"), "this string text, string separator, int width", Dash));
	}

	/// <summary>
	/// The same method called as the static it really is. The receiver is written this time, so the
	/// slot it takes is a real one -- and the same declaration therefore has two right answers.
	/// </summary>
	[Test]
	public void Rewrites_the_same_extension_method_called_as_a_static()
	{
		Assert.Equal(
			"""(a, "-", 4)""",
			Rewrite(Extension("Target(a, 4)"), "this string text, string separator, int width", Dash));
	}

	/// <summary>
	/// A call site somebody wrapped across lines stays wrapped, the argument arriving in the middle
	/// of it included.
	/// <para>
	/// It used to be taken apart. The commas already there were kept, so the arguments already there
	/// kept their lines, and nothing gave the new one any of it: the comma the list gained was a
	/// comma and a space rather than a comma and the break its neighbours use, and the new argument
	/// carried no indentation at all. So it landed at column zero and pulled the argument after it
	/// up onto the same line.
	/// </para>
	/// <para>
	/// Nothing downstream put it back. The whitespace pass runs over the spans the change annotated,
	/// which are the declarations' parameter lists and not the call sites, and Roslyn's formatter has
	/// no rule about where a continuation line sits -- so that is what reached disk.
	/// </para>
	/// </summary>
	[Test]
	public void Keeps_the_wrapping_of_a_call_site_it_inserts_into()
	{
		Assert.Equal("(\n\t\ta,\n\t\t\"-\",\n\t\tb)", Rewrite(WrappedCall, Inserted, Dash));
	}

	/// <summary>
	/// An argument that has to be named at a wrapped call site keeps the whitespace in front of it in
	/// front of it, rather than between the name and the value.
	/// <para>
	/// An argument's leading trivia sits on its first token, and naming one puts a new token in front
	/// -- so the break and the indentation ended up after the colon, as
	/// <c>filePath: \t\t\tTestContext.Current.CancellationToken</c>. That is what the finding behind
	/// the binding work reported alongside the argument being on the wrong parameter, and getting the
	/// parameter right did not move it.
	/// </para>
	/// </summary>
	[Test]
	public void Keeps_the_whitespace_in_front_of_an_argument_it_names()
	{
		// A new optional in front of second is what forces second to be named at all: an argument
		// stays positional only while it would land in its own slot.
		var wanted = "string first, string separator, string third = \"\", string second";

		Assert.Equal("(\n\t\ta,\n\t\t\"-\",\n\t\tsecond: b)", Rewrite(WrappedCall, wanted, Dash));
	}

	/// <summary>
	/// The same whitespace, on an argument whose name is kept rather than written. Its break and
	/// indentation sit on the name, so a pass that rebuilds the colon has to put them back in front of
	/// it -- and a pass that took the name off would take them with it and land the argument at column
	/// zero, which is what four call sites of one wrapped method did when their arguments no longer
	/// needed naming. Nothing takes a name off now, so this is the shape that has to hold.
	/// </summary>
	[Test]
	public void Keeps_the_whitespace_in_front_of_a_wrapped_argument_that_keeps_its_name()
	{
		var call = "public static class Fixture\n"
			+ "{\n"
			+ "\tpublic static string Target(string first, string second) => first + second;\n"
			+ "\n"
			+ "\tpublic static string Use(string a, string b) => Target(\n"
			+ "\t\tfirst: a,\n"
			+ "\t\tsecond: b);\n"
			+ "}\n";

		Assert.Equal("(\n\t\tfirst: a,\n\t\t\"-\",\n\t\tsecond: b)", Rewrite(call, Inserted, Dash));
	}

	/// <summary>
	/// Where the call sits does not change what its arguments mean, so an expression-bodied member
	/// and a block body have to give the same answer. Asserted together rather than separately,
	/// because the claim is that they agree.
	/// </summary>
	[Test]
	public void Answers_the_same_inside_an_expression_body_and_a_block()
	{
		var block = """
			public static class Fixture
			{
				public static string Target(string first, string second) => first + second;

				public static string Use(string a, string b)
				{
					return Target(a, b);
				}
			}
			""";

		Assert.Equal(
			Rewrite(Calling("Target(a, b)"), Inserted, Dash),
			Rewrite(block, Inserted, Dash));
	}

	/// <summary>
	/// A call inside a lambda, which is the shape a test fixture or a LINQ chain writes most often.
	/// The lambda is a different symbol from the method holding it, and the reference search reaches
	/// inside it all the same.
	/// </summary>
	[Test]
	public void Rewrites_a_call_written_inside_a_lambda()
	{
		var source = """
			using System;

			public static class Fixture
			{
				public static string Target(string first, string second) => first + second;

				public static Func<string> Use(string a, string b) => () => Target(a, b);
			}
			""";

		Assert.Equal("""(a, "-", b)""", Rewrite(source, Inserted, Dash));
	}

	/// <summary>The ordinary fixture: a two-parameter target, called once, as the case writes it.</summary>
	private static string Calling(string call) => $$"""
		public static class Fixture
		{
			public static string Target(string first, string second) => first + second;

			public static string Use(string a, string b) => {{call}};
		}
		""";

	/// <summary>The same, with the target declared as an extension method.</summary>
	private static string Extension(string call) => $$"""
		public static class Fixture
		{
			public static string Target(this string text, int width) => text;

			public static string Use(string a) => {{call}};
		}
		""";

	/// <summary>
	/// The rewritten argument list as text, or null where the call site was left alone. The
	/// declaration in the source supplies the old parameters, so a case says its shape once rather
	/// than twice.
	/// </summary>
	private static string? Rewrite(string source, string wanted, params string[] arguments) =>
		CallSites.Rewrite(source, wanted, arguments);
}
