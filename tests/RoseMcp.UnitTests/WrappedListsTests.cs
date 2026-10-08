using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// The one shape of continuation layout <c>rose_format</c> can tell mechanically: items of one wrapped
/// list beginning their lines at different depths.
/// <para>
/// The other half matters as much as the finding, because a notice that fires on a convention is one
/// nobody reads. A list wrapped two levels deep throughout is a choice some repositories make, a list
/// sharing its continuation lines is ordinary, and a tab is the same depth as the spaces it stands
/// for; none of those is reported.
/// </para>
/// </summary>
public sealed class WrappedListsTests
{
	/// <summary>
	/// A splice that added the destination's indentation to code that already carried it: two elements
	/// at four tabs between neighbours at two, which formats clean and passes dotnet format.
	/// </summary>
	[Test]
	public void Names_the_items_of_a_list_written_deeper_than_their_neighbours()
	{
		var source = Lines(
			"class C",
			"{",
			"\tstring[] Names =",
			"\t[",
			"\t\t\"a\",",
			"\t\t\t\t\"b\",",
			"\t\t\t\t\"c\",",
			"\t\t\"d\",",
			"\t];",
			"}");

		Disagreeing(source).ShouldBe([6, 7]);
	}

	/// <summary>Where the first item is the odd one out, it is the one named rather than everything after it.</summary>
	[Test]
	public void Takes_the_depth_most_of_the_list_shares_as_meant()
	{
		var source = Lines(
			"class C",
			"{",
			"\tint M() => Sum(",
			"\t\t\t\t1,",
			"\t\t2,",
			"\t\t3);",
			"\tint Sum(int a, int b, int c) => a + b + c;",
			"}");

		Disagreeing(source).ShouldBe([4]);
	}

	[Test]
	public void Says_nothing_about_a_list_wrapped_two_levels_deep_throughout()
	{
		var source = Lines(
			"class C",
			"{",
			"\tint M() => Sum(",
			"\t\t\t\t1,",
			"\t\t\t\t2,",
			"\t\t\t\t3);",
			"\tint Sum(int a, int b, int c) => a + b + c;",
			"}");

		Disagreeing(source).ShouldBeEmpty();
	}

	/// <summary>
	/// Only an item that begins its line has a depth. One following a comma on a shared line, or the
	/// first argument kept on the line that opens the call, is not compared with anything.
	/// </summary>
	[Test]
	public void Compares_only_the_items_that_begin_a_line()
	{
		var source = Lines(
			"class C",
			"{",
			"\tint M() => Sum(1,",
			"\t\t2, 3) + Sum(",
			"\t\t4, 5,",
			"\t\t6);",
			"\tint Sum(int a, int b, int c) => a + b + c;",
			"}");

		Disagreeing(source).ShouldBeEmpty();
	}

	[Test]
	public void Counts_a_tab_as_the_spaces_it_stands_for()
	{
		var source = Lines(
			"class C",
			"{",
			"\tvoid M(",
			"\t\tint a,",
			"\t    int b)",
			"\t{",
			"\t}",
			"}");

		Disagreeing(source).ShouldBeEmpty();
	}

	/// <summary>Branches of an <c>#if</c> may be laid out for different builds, so a list holding one is not judged.</summary>
	[Test]
	public void Passes_over_a_list_holding_a_directive()
	{
		var source = Lines(
			"class C",
			"{",
			"\tint[] Values =",
			"\t[",
			"\t\t1,",
			"#if DEBUG",
			"\t\t\t\t2,",
			"#endif",
			"\t];",
			"}");

		Disagreeing(source).ShouldBeEmpty();
	}

	[Test]
	public void Names_the_file_and_the_line_and_says_nothing_changed_it()
	{
		var source = Lines(
			"class C",
			"{",
			"\tint M() => Sum(",
			"\t\t1,",
			"\t\t\t\t2,",
			"\t\t3);",
			"\tint Sum(int a, int b, int c) => a + b + c;",
			"}");

		var text = SourceText.From(source);
		var notice = WrappedLists.Notice(CSharpSyntaxTree.ParseText(text).GetRoot(), text, 4, "C.cs");

		notice.ShouldNotBeNull();
		notice.ShouldStartWith("C.cs: line 5 ", Case.Sensitive);
		notice.ShouldContain("nothing changed it", Case.Sensitive);
	}

	[Test]
	public void Has_nothing_to_say_about_a_file_whose_lists_agree()
	{
		var text = SourceText.From(Lines("class C", "{", "\tint[] Values = [1, 2];", "}"));

		WrappedLists.Notice(CSharpSyntaxTree.ParseText(text).GetRoot(), text, 4, "C.cs").ShouldBeNull();
	}

	private static IReadOnlyList<int> Disagreeing(string source)
	{
		var text = SourceText.From(source);

		return WrappedLists.Disagreeing(CSharpSyntaxTree.ParseText(text).GetRoot(), text, 4);
	}

	private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";
}
