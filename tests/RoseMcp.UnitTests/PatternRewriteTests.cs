using RoseMcp.Patterns;

using static RoseMcp.UnitTests.PatternHarness;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a replacement writes, and what it puts back: captures moved into the template as the caller
/// wrote them, parentheses only where the new position needs them, nested sites composed, and every
/// site whose replacement would not compile left exactly as it was.
/// </summary>
public sealed class PatternRewriteTests
{
	/// <summary>A capture moves into the template as the caller wrote it.</summary>
	[Test]
	public void Writes_each_capture_where_the_template_puts_it()
	{
		var (text, _) = Rewrite(
			"using Shouldly; class C { void M(int count) { Assert.Equal(1, count); } }",
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		text.ShouldContain("{ count.ShouldBe(1); }", Case.Sensitive);
	}

	/// <summary>
	/// A capture that becomes a receiver is parenthesised unless it cannot need it. A conditional access
	/// always is: without the parentheses the assertion joins the null check and silently does nothing.
	/// </summary>
	[Test]
	[Arguments("a?.B", "(a?.B).ShouldBe(1)")]
	[Arguments("x ?? 0", "(x ?? 0).ShouldBe(1)")]
	[Arguments("-y", "(-y).ShouldBe(1)")]
	[Arguments("a.C", "a.C.ShouldBe(1)")]
	[Arguments("F()", "F().ShouldBe(1)")]
	[Arguments("(y)", "(y).ShouldBe(1)")]
	public void Parenthesises_a_receiver_only_where_it_is_needed(string actual, string expected)
	{
		var (text, _) = Rewrite(
			$"using Shouldly; class A {{ public int B; public int C; }} class D {{ int F() => 0; void M(A? a, int? x, int y) {{ Assert.Equal<int?>(1, {actual}); }} }}",
			Rule("Assert.Equal<$T$>($e$, $a$)", "$a$.ShouldBe($e$)"));

		text.ShouldContain(expected, Case.Sensitive);
	}

	/// <summary>A comment inside a capture goes with it.</summary>
	[Test]
	public void Keeps_a_comment_inside_a_capture()
	{
		var (text, _) = Rewrite(
			"using Shouldly; class C { void M(int count) { Assert.Equal(/* one */ 1, count); } }",
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		text.ShouldContain("count.ShouldBe(/* one */ 1)", Case.Sensitive);
	}

	/// <summary>
	/// A null-forgiving operator on an argument goes with the capture. The operator is not an operation,
	/// so the argument can report only what is inside it; dropped, it writes back a nullable warning that
	/// is an error wherever warnings are.
	/// </summary>
	[Test]
	public void Keeps_a_null_forgiving_operator_on_a_capture()
	{
		var (text, _) = Rewrite(
			"using Shouldly; class C { void M(string? text) { Assert.Contains(\"a\", text!); } }",
			Rule("Assert.Contains($sub:string$, $s:string$)", "$s$.ShouldContain($sub$, Case.Sensitive)"));

		text.ShouldContain("text!.ShouldContain(\"a\", Case.Sensitive)", Case.Sensitive);
	}

	/// <summary>
	/// An argument the caller put on a line of its own stays on one, at the indentation it had, rather than
	/// being pulled up onto the call; a capture that moves to the receiver joins the call as it always did.
	/// </summary>
	[Test]
	public void Keeps_an_argument_on_the_line_the_caller_gave_it()
	{
		var (text, _) = Rewrite(
			"using Shouldly; class C { void M(string s) {\n\t\tAssert.Contains(\n\t\t\t\"a\",\n\t\t\ts,\n\t\t\tStringComparison.Ordinal);\n\t} }",
			Rule("Assert.Contains($sub:string$, $s:string$, StringComparison.Ordinal)", "$s$.ShouldContain($sub$, Case.Sensitive)"));

		text.ShouldContain("\t\ts.ShouldContain(\n\t\t\t\"a\", Case.Sensitive);", Case.Sensitive);
	}

	/// <summary>An argument that begins a line after the template's comma leaves no space at the end of the line before it.</summary>
	[Test]
	public void Leaves_no_space_before_an_argument_that_begins_a_line()
	{
		var (text, _) = Rewrite(
			"class C { void M(List<int> xs) {\n\t\tAssert.Contains(1,\n\t\t\txs);\n\t} }",
			Rule("Assert.Contains($item$, $xs$)", "Assert.Contains($item$, $xs$)"));

		text.ShouldContain("Assert.Contains(1,\n\t\t\txs);", Case.Sensitive);
	}

	/// <summary>A site inside another's capture is rewritten first, and the outer replacement takes the result.</summary>
	[Test]
	public void Composes_a_site_inside_another()
	{
		var (text, sites) = Rewrite(
			"using Shouldly; class C { void M(List<int> xs) { Assert.Equal(3, Assert.Single(xs)); } }",
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"),
			Rule("Assert.Single($xs$)", "$xs$.ShouldHaveSingleItem()"));

		text.ShouldContain("xs.ShouldHaveSingleItem().ShouldBe(3)", Case.Sensitive);
		foreach (var site in sites)
		{
			site.State.ShouldBe(SiteState.Rewritten);
		}
	}

	/// <summary>
	/// A statement rule writes a lambda's body into a loop, with the assertion inside it rewritten first
	/// and parenthesised where it became a receiver.
	/// </summary>
	[Test]
	public void Writes_a_lambda_body_into_a_loop()
	{
		var (text, _) = Rewrite(
			"using Shouldly; class C { void M(List<int> xs) { Assert.All(xs, x => Assert.True(x > 0)); } }",
			Rule("Assert.All($xs$, $x:id$ => $body$);", "foreach (var $x$ in $xs$) { $body$; }"),
			Rule("Assert.True($c$)", "$c$.ShouldBeTrue()"));

		text.ShouldContain("foreach (var x in xs) { (x > 0).ShouldBeTrue(); }", Case.Sensitive);
	}

	/// <summary>A block body becomes the loop's own block, rather than a block inside one.</summary>
	[Test]
	public void A_block_body_becomes_the_loops_block()
	{
		var (text, _) = Rewrite(
			"using Shouldly; class C { void M(List<int> xs) { Assert.All(xs, x => { Assert.True(x > 1); }); } }",
			Rule("Assert.All($xs$, $x:id$ => $body$);", "foreach (var $x$ in $xs$) { $body$; }"),
			Rule("Assert.True($c$)", "$c$.ShouldBeTrue()"));

		text.ShouldContain("foreach (var x in xs) { (x > 1).ShouldBeTrue(); }", Case.Sensitive);
		text.ShouldNotContain("{ {", Case.Sensitive);
	}

	/// <summary>A body that returns is refused: in a loop, the return would leave the method.</summary>
	[Test]
	public void Refuses_a_body_that_returns()
	{
		var (text, sites) = Rewrite(
			"class C { void M(List<int> xs) { Assert.All(xs, x => { if (x > 0) return; Assert.Fail(\"no\"); }); } }",
			Rule("Assert.All($xs$, $x:id$ => $body$);", "foreach (var $x$ in $xs$) { $body$; }"));

		var site = sites.ShouldHaveSingleItem();

		site.State.ShouldBe(SiteState.Skipped);
		site.Reason!.ShouldContain("returns", Case.Sensitive);
		text.ShouldContain("Assert.All(xs, x =>", Case.Sensitive);
	}

	/// <summary>
	/// A replacement that does not compile at its site is put back with the compiler's reason, and a
	/// site beside it in the same method is still written.
	/// </summary>
	[Test]
	public void Puts_back_a_replacement_that_does_not_compile_and_keeps_its_neighbour()
	{
		var (text, sites) = Rewrite(
			"using Shouldly; class C { void M(int count) { Assert.Equal(1L, count); Assert.Equal(2, count); } }",
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		sites.Select(site => site.State).ShouldBe([SiteState.Skipped, SiteState.Rewritten]);
		sites[0].DiagnosticId.ShouldStartWith("CS", Case.Sensitive);
		text.ShouldContain("Assert.Equal(1L, count); count.ShouldBe(2);", Case.Sensitive);
	}

	/// <summary>
	/// An error the replacement causes in the rest of its statement is charged to it: a value that is
	/// used, replaced by one that is not there.
	/// </summary>
	[Test]
	public void Charges_an_error_in_the_same_statement_to_the_site_in_it()
	{
		var (text, sites) = Rewrite(
			"using Shouldly; class C { void M(List<int> xs) { var one = Assert.Single(xs); } }",
			Rule("Assert.Single($xs$)", "$xs$.ShouldContain(1)"));

		sites.ShouldHaveSingleItem().State.ShouldBe(SiteState.Skipped);
		text.ShouldContain("var one = Assert.Single(xs);", Case.Sensitive);
	}

	/// <summary>
	/// A site whose rule's replacement is put back does not fall through to a later rule that also
	/// matches it. That fall-through is how a string check would land on the collection rule with its
	/// arguments reversed.
	/// </summary>
	[Test]
	public void A_site_put_back_does_not_fall_through_to_a_later_rule()
	{
		var (text, sites) = Rewrite(
			"using Shouldly; class C { void M() { Assert.Contains(\"b\", \"abc\"); } }",
			Rule("Assert.Contains($sub:string$, $s:string$)", "$s$.NoSuchMethod($sub$)"),
			Rule("Assert.Contains($item$, $xs$)", "$xs$.ShouldContain($item$)"));

		var site = sites.ShouldHaveSingleItem();

		((site.Site.Rule.Number, site.State)).ShouldBe((1, SiteState.Skipped));
		text.ShouldContain("Assert.Contains(\"b\", \"abc\");", Case.Sensitive);
	}

	/// <summary>
	/// A file still gaining errors when the rounds run out is left as it was, and only that file: one
	/// that converged keeps its rewrite. Reverting every file with sites left was how one stubborn file
	/// took a whole project's rewrite with it.
	/// </summary>
	[Test]
	public void A_file_out_of_rounds_does_not_take_another_files_rewrite()
	{
		var rewrites = RewriteEach(
			1,
			[
				"using Shouldly; class A { void M(int count) { Assert.Equal(1L, count); Assert.Equal(2, count); } }",
				"using Shouldly; class B { void M(int count) { Assert.Equal(3, count); } }",
			],
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		rewrites[0].Sites.Select(site => site.State).ShouldBe([SiteState.Skipped, SiteState.Skipped]);
		rewrites[1].Sites.ShouldHaveSingleItem().State.ShouldBe(SiteState.Rewritten);
		rewrites[1].Root!.ToFullString().ShouldContain("count.ShouldBe(3)", Case.Sensitive);
	}

	/// <summary>Everything outside the sites is left byte for byte as it was.</summary>
	[Test]
	public void Leaves_everything_outside_the_sites_as_it_was()
	{
		const string Around = "// keep this\n\tint   spacing = 1 ;";
		var (text, _) = Rewrite(
			$"using Shouldly; class C {{ void M(int count) {{\n\t{Around}\n\tAssert.Equal(1, count); }} }}",
			Rule("Assert.Equal($e$, $a$)", "$a$.ShouldBe($e$)"));

		text.ShouldContain(Around, Case.Sensitive);
	}
}
