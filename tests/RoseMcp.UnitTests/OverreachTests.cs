using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// The measure of what an edit changed besides what it was asked to. Every writing tool reports
/// through it, so a wrong answer here is either a false alarm on every edit or silence over the
/// damage it exists to name -- which is why the shapes the writing tools actually produce are pinned
/// here, rather than left to be discovered through them.
/// </summary>
public sealed class OverreachTests
{
	[Test]
	public void Says_nothing_about_a_change_inside_what_was_asked()
	{
		var before = "first\nsecond\nthird\n";

		var reach = Overreach.Of(Text(before), Text("first\nSECOND\nthird\n"), [Line(before, "second")]);

		Assert.False(reach.Any);
		Assert.Null(reach.Sentence("File.cs"));
	}

	[Test]
	public void Names_a_line_that_changed_outside_what_was_asked()
	{
		var before = "first\nsecond\nthird\nfourth\n";

		var reach = Overreach.Of(Text(before), Text("first\nSECOND\nTHIRD\nfourth\n"), [Line(before, "second")]);

		Assert.Equal([3], reach.Lines);
		Assert.Empty(reach.Insertions);
	}

	/// <summary>
	/// A line that went away is as much a change as one rewritten, and the case that matters is the
	/// comment between two statements a match covered: find cannot carry a comment, so a caller cannot
	/// have asked for one to go.
	/// </summary>
	[Test]
	public void Names_a_line_that_went_away_outside_what_was_asked()
	{
		var before = "\tone();\n\t// why the next line exists\n\ttwo();\n";
		var after = "\treplaced();\n";

		var asked = new[] { Token(before, "one();"), Token(before, "two();") };

		var reach = Overreach.Of(Text(before), Text(after), asked);

		Assert.Equal([2], reach.Lines);
	}

	[Test]
	public void Takes_lines_that_went_in_beside_an_asked_line_as_asked()
	{
		var before = "first\nsecond\nthird\n";

		var reach = Overreach.Of(Text(before), Text("first\nsecond\nadded\nthird\n"), [Line(before, "second")]);

		Assert.False(reach.Any);
	}

	[Test]
	public void Names_lines_that_went_in_away_from_anything_asked()
	{
		var before = "first\nsecond\nthird\nfourth\nfifth\n";

		var reach = Overreach.Of(
			Text(before), Text("first\nsecond\nthird\nfourth\nadded\nalso\nfifth\n"), [Line(before, "first")]);

		Assert.Empty(reach.Lines);
		Assert.Equal([(4, 2)], reach.Insertions);
	}

	/// <summary>
	/// An insertion asks for the place it goes and nothing more. At the start of a line that is the gap
	/// in front of it, so the statement above the gap is still the caller's to have left alone -- which
	/// is exactly how an insertion that reflows the body it goes into gets caught.
	/// </summary>
	[Test]
	public void Asks_only_for_the_gap_where_an_insertion_goes_at_the_start_of_a_line()
	{
		var before = "{\n\tfirst();\n}\n";
		var place = new TextSpan(before.IndexOf('}', StringComparison.Ordinal), 0);

		var clean = Overreach.Of(Text(before), Text("{\n\tfirst();\n\tadded();\n}\n"), [place]);
		var reflowed = Overreach.Of(Text(before), Text("{\n\t\tfirst();\n\tadded();\n}\n"), [place]);

		Assert.False(clean.Any);
		Assert.Equal([2], reflowed.Lines);
	}

	/// <summary>
	/// Inside a line, an insertion rewrites the line it goes into -- a value added after the last one of
	/// an enum gives that one a comma -- so the line is asked for as well as the lines that follow it.
	/// </summary>
	[Test]
	public void Asks_for_the_line_an_insertion_goes_inside()
	{
		var before = "{\n\tFirst,\n\tSecond\n}\n";
		var place = new TextSpan(before.IndexOf("Second", StringComparison.Ordinal) + "Second".Length, 0);

		var reach = Overreach.Of(Text(before), Text("{\n\tFirst,\n\tSecond,\n\tThird\n}\n"), [place]);

		Assert.False(reach.Any);
	}

	/// <summary>
	/// Adding a member between two others separated by a blank line: the diff may pair the old blank
	/// line with the one above the new member or the one below it, and which it picks says nothing
	/// about what the edit did.
	/// </summary>
	[Test]
	public void Reaches_an_insertion_across_blank_lines_from_where_it_was_asked_for()
	{
		var before = "class C\n{\n\tvoid A() { }\n\n\tvoid B() { }\n}\n";
		var place = new TextSpan(before.IndexOf("\n\n", StringComparison.Ordinal) + 1, 0);

		var reach = Overreach.Of(
			Text(before), Text("class C\n{\n\tvoid A() { }\n\n\tvoid X() { }\n\n\tvoid B() { }\n}\n"), [place]);

		Assert.False(reach.Any);
	}

	/// <summary>
	/// Deleting a member between two blank lines leaves one of them, and the diff may keep the one the
	/// member carried and drop the other -- which is the same deletion, and not a line nobody asked for.
	/// </summary>
	[Test]
	public void Takes_a_blank_line_beside_what_was_asked_as_part_of_it()
	{
		var before = "class C\n{\n\tvoid A() { }\n\n\tvoid B() { }\n\n\tvoid D() { }\n}\n";
		var member = TextSpan.FromBounds(
			before.IndexOf("\n\n\tvoid B", StringComparison.Ordinal) + 1,
			before.IndexOf("\n\n\tvoid D", StringComparison.Ordinal) + 1);

		var reach = Overreach.Of(Text(before), Text("class C\n{\n\tvoid A() { }\n\n\tvoid D() { }\n}\n"), [member]);

		Assert.False(reach.Any);
	}

	/// <summary>
	/// A new group of imports goes in after a blank line of its own, so the diff shows it on the far side
	/// of the blank line that separated the imports from the namespace.
	/// </summary>
	[Test]
	public void Takes_an_insertion_past_a_blank_line_beside_an_asked_line_as_asked()
	{
		var before = "using System;\n\nnamespace N;\n";

		var reach = Overreach.Of(
			Text(before), Text("using System;\n\nusing static System.Math;\n\nnamespace N;\n"), [Line(before, "using System;")]);

		Assert.False(reach.Any);
	}

	[Test]
	public void Names_every_change_to_a_file_nothing_was_asked_of()
	{
		var reach = Overreach.Of(Text("first\nsecond\n"), Text("first\nother\n"), []);

		Assert.Equal([2], reach.Lines);
	}

	/// <summary>
	/// A terminator is not line content, and rewriting a file's endings is the whitespace pass's job
	/// within what was written -- said in a sentence of its own, since no diff can show it.
	/// </summary>
	[Test]
	public void Says_nothing_about_line_endings_alone()
	{
		var reach = Overreach.Of(Text("first\r\nsecond\r\n"), Text("first\nsecond\n"), []);

		Assert.False(reach.Any);
	}

	[Test]
	public void Numbers_the_lines_as_the_file_was_before_the_edit()
	{
		var before = "1\n2\n3\n4\n5\n6\n7\n8\n9\n";

		var reach = Overreach.Of(Text(before), Text("1\n2\nC\nD\n5\n6\nG\n8\n9\nnew\nnewer\n"), []);

		Assert.StartsWith(
			"Greeter.cs: lines 3-4 and 7 changed and 2 lines went in after line 9.",
			reach.Sentence("Greeter.cs"),
			StringComparison.Ordinal);
	}

	[Test]
	public void Counts_the_places_it_does_not_name()
	{
		var before = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n";

		var reach = Overreach.Of(Text(before), Text("A\n2\nC\n4\nE\n6\nG\n8\nI\n10\nK\n12\n"), []);

		Assert.StartsWith("Many.cs: lines 1, 3, 5, 7 and 2 more place(s) changed.", reach.Sentence("Many.cs"), StringComparison.Ordinal);
	}

	private static SourceText Text(string text) => SourceText.From(text);

	/// <summary>The span of the line holding <paramref name="content"/>, as a tool asking for it would give it.</summary>
	private static TextSpan Line(string text, string content)
	{
		var start = text.IndexOf(content, StringComparison.Ordinal);

		return new TextSpan(start, content.Length);
	}

	/// <summary>The span of one piece of code, as the token path reports the tokens it matched.</summary>
	private static TextSpan Token(string text, string code) => Line(text, code);
}
