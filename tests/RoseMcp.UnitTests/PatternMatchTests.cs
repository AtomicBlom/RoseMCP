using RoseMcp.Patterns;

using static RoseMcp.UnitTests.PatternHarness;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a find matches: by what a call binds to, never by how it is spelled.
/// <para>
/// Each case is a promise the decision record makes -- a named argument, an alias, a <c>using static</c>
/// and either form of an extension call all match; an overload the shape does not fit, a default
/// that is not left alone, a type argument the caller chose, do not -- written as the smallest source
/// that shows it.
/// </para>
/// </summary>
public sealed class PatternMatchTests
{
	/// <summary>Arguments map to parameters by the compiler's answer, so a named, reordered call captures the same way.</summary>
	[Test]
	public void Captures_named_and_reordered_arguments_by_their_parameter()
	{
		var result = Scan("class C { void M(int x) { Assert.Equal(actual: x, expected: 1); } }", Rule("Assert.Equal($e$, $a$)"));

		Assert.Equal(["1: Assert.Equal(actual: x, expected: 1) [a=x, e=1]"], Sites(result));
	}

	/// <summary>A name in the find resolves as it would in the project, so the alias, the qualified name and a static import all reach it.</summary>
	[Test]
	[Arguments("Assert.Equal(1, x)")]
	[Arguments("Xunit.Assert.Equal(1, x)")]
	[Arguments("Equal(1, x)")]
	public void Matches_however_the_type_is_named(string call)
	{
		var result = Scan($"using static Xunit.Assert; class C {{ void M(int x) {{ {call}; }} }}", Rule("Assert.Equal($e$, $a$)"));

		Assert.Equal([$"1: {call} [a=x, e=1]"], Sites(result));
	}

	/// <summary>A generic method matches for every type argument it is called with, since what is compared is its definition.</summary>
	[Test]
	public void Matches_a_generic_method_whatever_it_is_instantiated_with()
	{
		var result = Scan(
			"class C { void M(int x, double d, List<int> xs) { Assert.Equal(1, x); Assert.Equal(1.5, d); Assert.Equal(xs, xs); } }",
			Rule("Assert.Equal($e$, $a$)"));

		Assert.Equal(3, result.Sites.Count);
	}

	/// <summary>
	/// An overload whose required parameters the find does not fill is outside it, and is reported as a
	/// call nobody matched -- which is how a catalog is known to be incomplete.
	/// </summary>
	[Test]
	public void Leaves_an_overload_the_shape_does_not_fit_and_reports_it()
	{
		var result = Scan("class C { void M(double d) { Assert.Equal(1.0, d, 3); } }", Rule("Assert.Equal($e$, $a$)"));

		Assert.Empty(result.Sites);
		Assert.Equal("Xunit.Assert.Equal(double, double, int)", Assert.Single(result.Unmatched).Method.ToString());
	}

	/// <summary>
	/// A parameter the find leaves out must be left to its default. One that spells the default out is
	/// still the plain call; one that asks for anything else is not, so a rule for the plain call never
	/// swallows a call that asked for more.
	/// </summary>
	[Test]
	[Arguments("Assert.Equal(\"a\", s)", true)]
	[Arguments("Assert.Equal(\"a\", s, ignoreCase: false)", true)]
	[Arguments("Assert.Equal(\"a\", s, ignoreCase: true)", false)]
	[Arguments("Assert.Equal(\"a\", s, true)", false)]
	public void Leaves_a_call_that_passes_more_than_the_default(string call, bool matches)
	{
		var result = Scan($"class C {{ void M(string s) {{ {call}; }} }}", Rule("Assert.Equal($e$, $a$)"));

		Assert.Equal(matches, result.Sites.Count == 1);
	}

	/// <summary>A constant in a find matches the same value of the same type, however the site spells or places it.</summary>
	[Test]
	[Arguments("Assert.Equal(\"a\", s, ignoreCase: true)", true)]
	[Arguments("Assert.Equal(\"a\", s, true)", true)]
	[Arguments("Assert.Equal(\"a\", s, ignoreCase: false)", false)]
	[Arguments("Assert.Equal(\"a\", s, true, ignoreWhiteSpaceDifferences: true)", false)]
	public void Matches_a_constant_argument_by_value(string call, bool matches)
	{
		var result = Scan(
			$"class C {{ void M(string s) {{ {call}; }} }}",
			Rule("Assert.Equal($e:string$, $a:string$, ignoreCase: true)"));

		Assert.Equal(matches, result.Sites.Count == 1);
	}

	/// <summary>
	/// An enum member is a constant too, so <c>StringComparison.Ordinal</c> matches wherever it is
	/// written and in whatever spelling -- but not a local holding it, whose value is not known here.
	/// </summary>
	[Test]
	[Arguments("Assert.Contains(\"a\", s, StringComparison.Ordinal)", true)]
	[Arguments("Assert.Contains(\"a\", s, comparisonType: System.StringComparison.Ordinal)", true)]
	[Arguments("Assert.Contains(\"a\", s, StringComparison.OrdinalIgnoreCase)", false)]
	[Arguments("var ordinal = StringComparison.Ordinal; Assert.Contains(\"a\", s, ordinal)", false)]
	public void Matches_an_enum_member_by_value(string call, bool matches)
	{
		var result = Scan(
			$"class C {{ void M(string s) {{ {call}; }} }}",
			Rule("Assert.Contains($sub:string$, $s:string$, StringComparison.Ordinal)"));

		Assert.Equal(matches, result.Sites.Count == 1);
	}

	/// <summary>
	/// A typed placeholder filters what it captures by the capture's own type: a char passed where the
	/// generic overload takes it does not satisfy <c>:string</c>, so the string rule leaves the call.
	/// </summary>
	[Test]
	public void A_typed_placeholder_checks_the_captures_own_type()
	{
		var result = Scan(
			"class C { void M() { Assert.Contains('c', \"abc\"); Assert.Contains(\"b\", \"abc\"); } }",
			Rule("Assert.Contains($sub:string$, $s:string$)"));

		Assert.Equal(["1: Assert.Contains(\"b\", \"abc\") [s=\"abc\", sub=\"b\"]"], Sites(result));
	}

	/// <summary>
	/// A type filters; it never picks the overload. <c>Single($xs:IEnumerable$)</c> still covers the
	/// generic overload, which a picked overload -- the non-generic one -- would have missed.
	/// </summary>
	[Test]
	public void A_typed_placeholder_does_not_pick_an_overload()
	{
		const string Source = "class C { void M(List<int> xs) { var one = Assert.Single(xs); } }";
		var rule = Rule("Assert.Single($xs:System.Collections.IEnumerable$)");

		var covered = Bind(Source, withStubs: true, rule).Bound[0].Methods.Select(method => method.ToString());

		Assert.Contains("Xunit.Assert.Single<T>(System.Collections.Generic.IEnumerable<T>)", covered);
		Assert.Single(Scan(Source, rule).Sites);
	}

	/// <summary>
	/// Two overloads with the same shape and their roles reversed are told apart by naming the
	/// parameter an argument is for, which fits only the overloads that have it.
	/// </summary>
	[Test]
	public void A_named_argument_pins_the_overload_that_has_that_parameter()
	{
		var result = Scan(
			"class C { void M(List<int> xs) { Assert.Contains(xs, x => x > 1); Assert.Contains(1, xs); } }",
			Rule("Assert.Contains($xs$, filter: $p$)"),
			Rule("Assert.Contains($item$, $xs$)"));

		Assert.Equal(
			["1: Assert.Contains(xs, x => x > 1) [p=x => x > 1, xs=xs]", "2: Assert.Contains(1, xs) [item=1, xs=xs]"],
			Sites(result));
	}

	/// <summary>
	/// The same two rules without the name are refused, because the first would take every item check
	/// with the collection and the item swapped -- and the second, meant for them, would never match.
	/// </summary>
	[Test]
	public void Refuses_a_rule_an_earlier_one_shadows()
	{
		var error = Assert.Throws<PatternException>(() => Scan(
			"class C { }",
			Rule("Assert.Contains($xs$, $p$)"),
			Rule("Assert.Contains($item$, $xs$)")));

		Assert.StartsWith("Rule 2 can never match: rule 1 comes first", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A more specific rule goes first and wins; the general one after it still matches what is left,
	/// and a site both match records the loser.
	/// </summary>
	[Test]
	public void The_first_rule_to_match_wins_and_the_rest_are_recorded()
	{
		var result = Scan(
			"class C { void M(bool a, bool b) { Assert.True(!(a && b)); Assert.True(a); } }",
			Rule("Assert.True(!$c$)"),
			Rule("Assert.True($c$)"));

		Assert.Equal(["1: Assert.True(!(a && b)) [c=(a && b)]", "2: Assert.True(a) [c=a]"], Sites(result));
		Assert.Equal([2], result.Sites[0].AlsoMatched);
	}

	/// <summary>The general rule first shadows the specific one, which is refused.</summary>
	[Test]
	public void A_general_rule_before_a_specific_one_is_refused()
	{
		var error = Assert.Throws<PatternException>(() => Scan("class C { }", Rule("Assert.True($c$)"), Rule("Assert.True(!$c$)")));

		Assert.Contains("Rule 2 can never match", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A type placeholder captures the type argument as written; the overload with a parameter the find
	/// does not fill is left.
	/// </summary>
	[Test]
	public void Captures_a_type_argument()
	{
		var result = Scan(
			"class C { void F() { } void M() { Assert.Throws<ArgumentException>(() => F()); Assert.Throws<ArgumentException>(\"p\", () => F()); } }",
			Rule("Assert.Throws<$T$>($a$)"));

		Assert.Equal(["1: Assert.Throws<ArgumentException>(() => F()) [T=ArgumentException, a=() => F()]"], Sites(result));
	}

	/// <summary>
	/// A find that writes no type argument matches only a call that writes none, since a type argument
	/// the caller chose can change what is compared.
	/// </summary>
	[Test]
	public void Leaves_a_call_that_writes_a_type_argument_the_find_does_not()
	{
		var result = Scan("class C { void M(int x) { Assert.Equal<long>(1, x); } }", Rule("Assert.Equal($e$, $a$)"));

		Assert.Empty(result.Sites);
		Assert.Single(result.Unmatched);
	}

	/// <summary>
	/// A statement rule captures a lambda's parameter and body, expression or block, and the call inside
	/// the body is a site of its own.
	/// </summary>
	[Test]
	public void A_statement_rule_captures_a_lambdas_parameter_and_body()
	{
		var result = Scan(
			"class C { void M(List<int> xs) { Assert.All(xs, item => Assert.True(item > 0)); Assert.All(xs, item => { Assert.True(item > 1); }); } }",
			Rule("Assert.All($xs$, $x:id$ => $body$);"),
			Rule("Assert.True($c$)"));

		Assert.Equal(
			[
				"1: Assert.All(xs, item => Assert.True(item > 0)); [body=Assert.True(item > 0), x=item, xs=xs]",
				"2: Assert.True(item > 0) [c=item > 0]",
				"1: Assert.All(xs, item => { Assert.True(item > 1); }); [body={ Assert.True(item > 1); }, x=item, xs=xs]",
				"2: Assert.True(item > 1) [c=item > 1]",
			],
			Sites(result));
	}

	/// <summary>An async lambda is not captured, since a loop cannot hold an await it did not have.</summary>
	[Test]
	public void Leaves_an_async_lambda()
	{
		var result = Scan(
			"class C { void M(List<int> xs) { Assert.All(xs, async item => await System.Threading.Tasks.Task.Yield()); } }",
			Rule("Assert.All($xs$, $x:id$ => $body$);"));

		Assert.Empty(result.Sites);
	}

	/// <summary>An extension method matches in either form, with the receiver captured as its first argument.</summary>
	[Test]
	public void Matches_an_extension_method_in_either_form()
	{
		var result = Scan(
			"using Checks; class C { void M(int x) { x.ShouldBe(1); Checks.Extensions.ShouldBe(x, 2); } }",
			[Rule("$a$.ShouldBe($e$)")],
			["Checks"]);

		Assert.Equal(["1: x.ShouldBe(1) [a=x, e=1]", "1: Checks.Extensions.ShouldBe(x, 2) [a=x, e=2]"], Sites(result));
	}

	/// <summary>Calls nest, and each is a site of its own.</summary>
	[Test]
	public void Finds_a_site_inside_another()
	{
		var result = Scan(
			"class C { void M(List<int> xs) { Assert.Equal(3, Assert.Single(xs)); } }",
			Rule("Assert.Equal($e$, $a$)"),
			Rule("Assert.Single($xs$)"));

		Assert.Equal(["1: Assert.Equal(3, Assert.Single(xs)) [a=Assert.Single(xs), e=3]", "2: Assert.Single(xs) [xs=xs]"], Sites(result));
	}

	/// <summary>
	/// A call into the same type that no rule is written for is reported by the method it calls, so a
	/// catalog's gaps are listed rather than left to be found by the build.
	/// </summary>
	[Test]
	public void Reports_calls_no_rule_matched_by_the_method_they_call()
	{
		var result = Scan("class C { void M(int x) { Assert.Equal(1, x); Assert.Fail(\"no\"); } }", Rule("Assert.Equal($e$, $a$)"));

		Assert.Equal("Xunit.Assert.Fail(string)", Assert.Single(result.Unmatched).Method.ToString());
	}

	/// <summary>A compilation without the names a rule uses leaves the rule unbound there, with the compiler's reason.</summary>
	[Test]
	public void Leaves_a_rule_unbound_where_its_names_do_not_exist()
	{
		var bound = Bind("class C { }", withStubs: false, Rule("Assert.Equal($e$, $a$)"));

		Assert.Empty(bound.Bound);
		Assert.StartsWith("Rule 1's find does not bind at `Assert.Equal`, because", bound.Unbound[1], StringComparison.Ordinal);
	}

	/// <summary>
	/// A using the compilation cannot resolve is not one a rewrite there can use, so a project that cannot
	/// see a catalog's namespace is not handed it to import; the forms a directive can take all resolve.
	/// </summary>
	[Test]
	public void Keeps_only_the_usings_a_compilation_resolves()
	{
		var bound = Bind(
			"class C { }",
			[Rule("Assert.True($c$)")],
			["Shouldly", "Nowhere.At.All", "static System.Math", "Text = System.Text"]);

		Assert.Equal(["Shouldly", "static System.Math", "Text = System.Text"], bound.Usings);
	}
}
