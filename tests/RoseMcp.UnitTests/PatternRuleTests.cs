using RoseMcp.Patterns;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a rule is before it meets a compilation: where its placeholders are, what each stands for,
/// and the refusals that teach the grammar to whoever is writing their first rule.
/// </summary>
public sealed class PatternRuleTests
{
	/// <summary>A placeholder's kind comes from where the parser put it.</summary>
	[Test]
	public void Settles_each_placeholders_kind_by_its_position()
	{
		var rule = Parse("Assert.All($xs$, $x$ => $body$);", "foreach (var $x$ in $xs$) { $body$; }");

		rule.Find.Placeholders["xs"].Kind.ShouldBe(PlaceholderKind.Expression);
		rule.Find.Placeholders["x"].Kind.ShouldBe(PlaceholderKind.Identifier);
		rule.Find.Placeholders["body"].Kind.ShouldBe(PlaceholderKind.Expression);
		rule.Find.IsStatement.ShouldBeTrue();
	}

	/// <summary>A placeholder in a type-argument list is a type, and may be repeated in a replace.</summary>
	[Test]
	public void A_type_argument_is_a_type_placeholder_and_may_repeat()
	{
		var rule = Parse("Assert.Throws<$T$>($a$)", "Should.Throw<$T$>($a$).ShouldBeOfType<$T$>()");

		rule.Find.Placeholders["T"].Kind.ShouldBe(PlaceholderKind.Type);
		rule.Replace.Placeholders["T"].Kind.ShouldBe(PlaceholderKind.Type);
	}

	/// <summary>A constraint is kept on the placeholder it narrows.</summary>
	[Test]
	public void Keeps_a_type_constraint()
	{
		var rule = Parse("Assert.Contains($sub:string$, $s:string?$)", "$s$.ShouldContain($sub$)");

		rule.Find.Placeholders["sub"].Constraint.ShouldBe("string");
		rule.Find.Placeholders["s"].Constraint.ShouldBe("string?");
	}

	/// <summary>A dollar sign inside a string literal is part of the string, and not a placeholder.</summary>
	[Test]
	public void A_dollar_inside_a_literal_is_not_a_placeholder()
	{
		var rule = Parse("Assert.Equal(\"$a$\", $b$)", "$b$.ShouldBe(\"$a$\")");

		rule.Find.Placeholders.Keys.ShouldBe(["b"]);
	}

	/// <summary>A replace may only use what its find captured, and the refusal names what it did capture.</summary>
	[Test]
	public void Refuses_a_replace_placeholder_the_find_does_not_capture()
	{
		var error = Refused("Assert.Equal($e$, $a$)", "$b$.ShouldBe($e$)");

		error.Message.ShouldBe("Rule 1's replace uses $b$, which its find does not capture. Its find captures $e$, $a$.");
	}

	/// <summary>A parse error is reported at the column the caller wrote, not the column of the rewritten code.</summary>
	[Test]
	public void Reports_a_parse_error_at_the_callers_own_column()
	{
		var error = Refused("Assert.Equal($expected$, )", "null");

		error.Message.ShouldContain("at column 26", Case.Sensitive);
		error.Message.ShouldContain(PatternException.Grammar, Case.Sensitive);
	}

	/// <summary>A find is a call, or a call written as a statement.</summary>
	[Test]
	[Arguments("$a$ + 1")]
	[Arguments("$a$.Length")]
	[Arguments("return $a$;")]
	public void Refuses_a_find_that_is_not_a_call(string find)
	{
		var error = Refused(find, "null");

		error.Message.ShouldStartWith("Rule 1's find has to be a call", Case.Sensitive);
	}

	/// <summary>:id is for an identifier, and is refused where an expression goes.</summary>
	[Test]
	public void Refuses_an_identifier_constraint_where_an_expression_goes()
	{
		var error = Refused("Assert.Equal($e:id$, $a$)", "null");

		error.Message.ShouldStartWith("Rule 1's find writes $e:id$ where an expression goes", Case.Sensitive);
	}

	/// <summary>A constraint belongs in the find, which is the side that decides what matches.</summary>
	[Test]
	public void Refuses_a_constraint_in_a_replace()
	{
		var error = Refused("Assert.Equal($e$, $a$)", "$a:string$.ShouldBe($e$)");

		error.Message.ShouldStartWith("Rule 1's replace constrains $a$", Case.Sensitive);
	}

	/// <summary>An expression written twice in a replace would be evaluated twice.</summary>
	[Test]
	public void Refuses_an_expression_written_twice_in_a_replace()
	{
		var error = Refused("Assert.Equal($e$, $a$)", "$a$.ShouldBe($a$)");

		error.Message.ShouldStartWith("Rule 1's replace writes $a$ 2 times", Case.Sensitive);
	}

	/// <summary>A placeholder written twice in a find would have to mean "the same code twice", which a find cannot say.</summary>
	[Test]
	public void Refuses_a_placeholder_written_twice_in_a_find()
	{
		var error = Refused("Assert.Equal($a$, $a$)", "null");

		error.Message.ShouldStartWith("Rule 1's find writes $a$ more than once", Case.Sensitive);
	}

	/// <summary>A placeholder that is never closed is named, with where it opens.</summary>
	[Test]
	public void Refuses_an_unclosed_placeholder()
	{
		var error = Refused("Assert.Equal($e, $a$)", "null");

		error.Message.ShouldContain("opens $e at column 14", Case.Sensitive);
	}

	/// <summary>Usings are checked as the directives they will be.</summary>
	[Test]
	public void Refuses_a_using_that_is_not_a_directives_target()
	{
		var error = Should.Throw<PatternException>(() => RuleCatalog.Parse([new RuleText("F($a$)", "G($a$)")], ["not a namespace!"])).ShouldBeOfType<PatternException>();

		error.Message.ShouldStartWith("'not a namespace!' is not something a using directive can name", Case.Sensitive);
	}

	/// <summary>The one rule of a single-rule catalog.</summary>
	private static Rule Parse(string find, string replace) => RuleCatalog.Parse([new RuleText(find, replace)]).Rules[0];

	/// <summary>The refusal for a rule that cannot be used.</summary>
	private static PatternException Refused(string find, string replace) =>
		Should.Throw<PatternException>(() => RuleCatalog.Parse([new RuleText(find, replace)])).ShouldBeOfType<PatternException>();
}
