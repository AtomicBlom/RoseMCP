
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

	/// <summary>
	/// A call wrapped once after its parenthesis, with its arguments sharing the continuation line --
	/// the shape a long argument list takes when it is wrapped to fit rather than to stack.
	/// </summary>
	private const string SharedLineCall = "public static class Fixture\n"
		+ "{\n"
		+ "\tpublic static string Target(string first, string second) => first + second;\n"
		+ "\n"
		+ "\tpublic static string Use(string a, string b) => Target(\n"
		+ "\t\ta, b);\n"
		+ "}\n";

	/// <summary>
	/// A call wrapped one argument to a line, with a comment ending the first argument's line. The
	/// comment sits in the comma's trailing trivia, which is what makes it easy to copy or lose with
	/// the comma.
	/// </summary>
	private const string CommentedCall = "public static class Fixture\n"
		+ "{\n"
		+ "\tpublic static string Target(string first, string second) => first + second;\n"
		+ "\n"
		+ "\tpublic static string Use(string a, string b) => Target(\n"
		+ "\t\ta, // the a\n"
		+ "\t\tb);\n"
		+ "}\n";

	/// <summary>The ordinary shape: every argument in its own place, and the new one lands between them.</summary>
	[Test]
	public void Puts_a_new_argument_between_two_positional_ones()
	{
		Rewrite(Calling("Target(a, b)"), Inserted, Dash).ShouldBe("""(a, "-", b)""");
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
		Rewrite(Calling("Target(first: a, second: b)"), Inserted, Dash).ShouldBe(
			"""(first: a, "-", second: b)""");
	}

	/// <summary>
	/// The mixed shape that works: the named argument is the trailing one. Its name is the caller's
	/// and stays; the positional one beside it had none to keep.
	/// </summary>
	[Test]
	public void Rewrites_a_trailing_named_argument_beside_a_positional_one()
	{
		Rewrite(Calling("Target(a, second: b)"), Inserted, Dash).ShouldBe("""(a, "-", second: b)""");
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
		Rewrite(Calling("Target(first: a, b)"), Inserted, Dash).ShouldBe("""(first: a, "-", b)""");
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

		Rewrite(source, """string first, string separator, string second = "x" """, Dash).ShouldBe("""(a, "-")""");
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

		Rewrite(source, "string first, string separator, params string[] rest", Dash).ShouldBe("""(a, "-", b, c)""");
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

		Rewrite(source, "string first, string separator, out int count, ref int value, in int size", Dash).ShouldBe(
			"""(a, "-", out var count, ref value, in size)""");
	}

	/// <summary>
	/// An extension method invoked on its receiver: the first parameter has an argument nowhere in
	/// the list, so every parameter after it is one place to the left of where the text suggests.
	/// </summary>
	[Test]
	public void Counts_the_receiver_of_an_extension_method_as_an_argument_that_is_not_there()
	{
		Rewrite(Extension("a.Target(4)"), "this string text, string separator, int width", Dash).ShouldBe(
			"""("-", 4)""");
	}

	/// <summary>
	/// The same method called as the static it really is. The receiver is written this time, so the
	/// slot it takes is a real one -- and the same declaration therefore has two right answers.
	/// </summary>
	[Test]
	public void Rewrites_the_same_extension_method_called_as_a_static()
	{
		Rewrite(Extension("Target(a, 4)"), "this string text, string separator, int width", Dash).ShouldBe(
			"""(a, "-", 4)""");
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
		Rewrite(WrappedCall, Inserted, Dash).ShouldBe("(\n\t\ta,\n\t\t\"-\",\n\t\tb)");
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

		Rewrite(WrappedCall, wanted, Dash).ShouldBe("(\n\t\ta,\n\t\t\"-\",\n\t\tsecond: b)");
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

		Rewrite(call, Inserted, Dash).ShouldBe("(\n\t\tfirst: a,\n\t\t\"-\",\n\t\tsecond: b)");
	}

	/// <summary>
	/// A call wrapped once after its parenthesis, with both arguments on the continuation line. The
	/// argument arriving between them goes in after a comma and a space, as they do.
	/// <para>
	/// Taking the indentation of the first argument that has any gives it the continuation's own --
	/// right where every argument begins a line, and here a run of tabs in the middle of one, between
	/// the comma and the new argument. Nothing reports that: it compiles, and the argument list is
	/// exactly what the change asked for.
	/// </para>
	/// </summary>
	[Test]
	public void Puts_a_new_argument_inline_where_the_arguments_share_a_continuation_line()
	{
		Rewrite(SharedLineCall, Inserted, Dash).ShouldBe("(\n\t\ta, \"-\", b)");
	}

	/// <summary>The same line, with the new argument after the last one.</summary>
	[Test]
	public void Appends_a_new_argument_inline_where_the_arguments_share_a_continuation_line()
	{
		Rewrite(SharedLineCall, "string first, string second, string separator", Dash).ShouldBe("(\n\t\ta, b, \"-\")");
	}

	/// <summary>
	/// The same line, with the new argument in front of the first. The new one begins the line, so it
	/// takes the indentation; the argument it displaces is now mid-line and gives its indentation up,
	/// or the tabs would sit after the new argument's comma instead.
	/// </summary>
	[Test]
	public void Puts_a_new_first_argument_at_the_start_of_a_shared_continuation_line()
	{
		Rewrite(SharedLineCall, "string separator, string first, string second", Dash).ShouldBe("(\n\t\t\"-\", a, b)");
	}

	/// <summary>
	/// One argument to a line, with the new argument in front of the first: it takes a line of its own,
	/// and the argument it displaces keeps one.
	/// </summary>
	[Test]
	public void Puts_a_new_first_argument_on_a_line_of_its_own_where_each_argument_has_one()
	{
		Rewrite(WrappedCall, "string separator, string first, string second", Dash).ShouldBe("(\n\t\t\"-\",\n\t\ta,\n\t\tb)");
	}

	/// <summary>
	/// A comment written in front of the argument a new one displaces stays in front of it. Only the
	/// whitespace before it is layout; the comment is the caller's.
	/// </summary>
	[Test]
	public void Keeps_the_comment_in_front_of_an_argument_a_new_first_argument_displaces()
	{
		var call = "public static class Fixture\n"
			+ "{\n"
			+ "\tpublic static string Target(string first, string second) => first + second;\n"
			+ "\n"
			+ "\tpublic static string Use(string a, string b) => Target(\n"
			+ "\t\t/* first */ a, b);\n"
			+ "}\n";

		Rewrite(call, "string separator, string first, string second", Dash).ShouldBe("(\n\t\t\"-\", /* first */ a, b)");
	}

	/// <summary>
	/// Taking out the parameter whose argument began the continuation line leaves the next argument
	/// beginning it, at the indentation the line had, rather than at column zero.
	/// </summary>
	[Test]
	public void Moves_the_indentation_onto_the_argument_that_begins_the_line_when_the_first_is_removed()
	{
		Rewrite(SharedLineCall, "string second").ShouldBe("(\n\t\tb)");
	}

	/// <summary>
	/// Taking out the first argument where the break follows it, rather than the parenthesis, leaves
	/// the next one directly after the parenthesis instead of on a line of its own behind it.
	/// </summary>
	[Test]
	public void Pulls_the_next_argument_up_to_the_parenthesis_when_the_first_is_removed_before_a_break()
	{
		var call = "public static class Fixture\n"
			+ "{\n"
			+ "\tpublic static string Target(string first, string second) => first + second;\n"
			+ "\n"
			+ "\tpublic static string Use(string a, string b) => Target(a,\n"
			+ "\t\tb);\n"
			+ "}\n";

		Rewrite(call, "string second").ShouldBe("(b)");
	}

	/// <summary>
	/// A blank line above an argument that a new first argument pushes along stays a blank line, with
	/// nothing on it. Indentation put in front of the argument's own break would leave a line holding
	/// nothing but tabs.
	/// </summary>
	[Test]
	public void Keeps_a_blank_line_above_a_displaced_argument_empty()
	{
		var call = Calling("Target(\n\t\ta,\n\n\t\tb)");

		Rewrite(call, "string separator, string first, string second", Dash).ShouldBe("(\n\t\t\"-\",\n\t\ta,\n\n\t\tb)");
	}

	/// <summary>
	/// A blank line between the parenthesis and the first argument, which a new first argument then
	/// follows on the same line. The indentation is the one after the blank line, so the new argument
	/// is not at column zero, and the displaced one gives up its breaks rather than leaving a trailing
	/// space after the comma in front of it.
	/// </summary>
	[Test]
	public void Indents_a_new_first_argument_from_below_a_blank_line()
	{
		var call = Calling("Target(\n\n\t\ta, b)");

		Rewrite(call, "string separator, string first, string second", Dash).ShouldBe("(\n\t\t\"-\", a, b)");
	}

	/// <summary>
	/// A comment ending the line after an argument's comma stays after that argument when another is
	/// added at the end. The comma the list gains is the shape of the last one without its comment;
	/// copied whole, it writes the comment a second time.
	/// </summary>
	[Test]
	public void Keeps_a_line_end_comment_once_when_an_argument_is_appended()
	{
		var text = Rewrite(CommentedCall, "string first, string second, string separator", Dash);

		text.ShouldBe("(\n\t\ta, // the a\n\t\tb,\n\t\t\"-\")");
		CountOf(text, "// the a").ShouldBe(1);
	}

	/// <summary>
	/// The same comment, with a new argument in front. The comment stays beside the argument it was
	/// written about rather than with the comma at its position, which would put it beside the new one.
	/// </summary>
	[Test]
	public void Keeps_a_line_end_comment_with_its_argument_when_one_is_inserted_in_front()
	{
		var text = Rewrite(CommentedCall, "string separator, string first, string second", Dash);

		text.ShouldBe("(\n\t\t\"-\",\n\t\ta, // the a\n\t\tb)");
		CountOf(text, "// the a").ShouldBe(1);
	}

	/// <summary>The same comment, with a new argument between the commented one and the next.</summary>
	[Test]
	public void Keeps_a_line_end_comment_with_its_argument_when_one_is_inserted_after_it()
	{
		var text = Rewrite(CommentedCall, Inserted, Dash);

		text.ShouldBe("(\n\t\ta, // the a\n\t\t\"-\",\n\t\tb)");
		CountOf(text, "// the a").ShouldBe(1);
	}

	/// <summary>
	/// The same comment, with the parameter after it removed, which takes away the comma that carried
	/// it. It is kept after its argument, and the break that ends it stays too, so the parenthesis is
	/// not commented out.
	/// </summary>
	[Test]
	public void Keeps_a_line_end_comment_when_the_comma_carrying_it_is_removed()
	{
		var text = Rewrite(CommentedCall, "string first");

		text.ShouldBe("(\n\t\ta // the a\n\t\t)");
		CountOf(text, "// the a").ShouldBe(1);
	}

	/// <summary>
	/// A comment between a comma and the argument after it, on one line, labels that argument, and
	/// goes with it when a new argument is put in front of it.
	/// </summary>
	[Test]
	public void Keeps_an_inline_comment_in_front_of_the_argument_it_labels()
	{
		Rewrite(Calling("Target(a, /* the b */ b)"), Inserted, Dash).ShouldBe("""(a, "-", /* the b */ b)""");
	}

	/// <summary>
	/// A comment kept after the last argument when its comma goes, where the parenthesis is on a line
	/// of its own. The parenthesis keeps its own indentation; the call's continuation indentation is
	/// only for one that has none, and added to its own it pushes the parenthesis a level too deep.
	/// </summary>
	[Test]
	public void Keeps_the_indentation_of_a_closing_parenthesis_on_its_own_line_after_a_kept_comment()
	{
		var call = Calling("Target(\n\t\ta, // the a\n\t\tb\n\t)");

		Rewrite(call, "string first").ShouldBe("(\n\t\ta // the a\n\t)");
	}

	/// <summary>
	/// An argument appended to a call whose closing parenthesis is on a line of its own -- the shape a
	/// cancellation token added on the end meets most. The break in front of the parenthesis stays in
	/// front of the parenthesis; left on the argument that was last, it puts the new comma at column
	/// zero and the parenthesis's indentation after the new argument.
	/// </summary>
	[Test]
	public void Appends_before_a_closing_parenthesis_on_its_own_line()
	{
		Rewrite(OwnLineParenthesis(string.Empty), "string first, string second, string separator", Dash).ShouldBe(
			"(\n\t\ta,\n\t\tb,\n\t\t\"-\"\n\t)");
	}

	/// <summary>
	/// The same, with a comment ending the line of the argument that was last. It stays beside that
	/// argument, after the comma the argument gains, rather than ahead of it.
	/// </summary>
	[Test]
	public void Appends_before_a_closing_parenthesis_on_its_own_line_keeping_the_last_comment()
	{
		var text = Rewrite(OwnLineParenthesis(" // the b"), "string first, string second, string separator", Dash);

		text.ShouldBe("(\n\t\ta,\n\t\tb, // the b\n\t\t\"-\"\n\t)");
		CountOf(text, "// the b").ShouldBe(1);
	}

	/// <summary>
	/// The last argument taken out of a call whose closing parenthesis is on a line of its own. The
	/// argument left last takes the break in front of the parenthesis, rather than the parenthesis
	/// following it on its line behind its own indentation.
	/// </summary>
	[Test]
	[Arguments("")]
	[Arguments(" // the b")]
	public void Removes_the_last_argument_before_a_closing_parenthesis_on_its_own_line(string comment)
	{
		Rewrite(OwnLineParenthesis(comment), "string first").ShouldBe("(\n\t\ta\n\t)");
	}

	/// <summary>
	/// A call with its parenthesis on a line of its own that the change does not touch comes back
	/// exactly as written: what is taken off the last argument to be placed is put back.
	/// </summary>
	[Test]
	public void Leaves_a_call_with_its_closing_parenthesis_on_its_own_line_as_written()
	{
		Rewrite(OwnLineParenthesis(" // the b"), "string first, string second, string separator = \"\"").ShouldBe(
			"(\n\t\ta,\n\t\tb // the b\n\t)");
	}

	/// <summary>
	/// An argument whose line opens with a directive, moved up behind the parenthesis by taking out
	/// the argument before it. A directive has to begin its line, so the break in front of it stays:
	/// directly after the parenthesis, it is CS1040.
	/// </summary>
	[Test]
	public void Keeps_a_directive_in_front_of_a_moved_argument_on_a_line_of_its_own()
	{
		var call = Calling("Target(a,\n#region r\n\t\tb\n#endregion\n\t)");

		Rewrite(call, "string second").ShouldBe("(\n#region r\n\t\tb\n#endregion\n\t)");
	}

	/// <summary>A call wrapped one argument to a line with its closing parenthesis on a line of its own.</summary>
	/// <param name="comment">Written after the last argument, before the parenthesis's line.</param>
	private static string OwnLineParenthesis(string comment) => Calling($"Target(\n\t\ta,\n\t\tb{comment}\n\t)");

	/// <summary>How many times a piece of text occurs, so a duplicated comment is caught by name.</summary>
	private static int CountOf(string? text, string piece) =>
		text is null ? 0 : (text.Length - text.Replace(piece, string.Empty, StringComparison.Ordinal).Length) / piece.Length;

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

		Rewrite(block, Inserted, Dash).ShouldBe(
			Rewrite(Calling("Target(a, b)"), Inserted, Dash));
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

		Rewrite(source, Inserted, Dash).ShouldBe("""(a, "-", b)""");
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
