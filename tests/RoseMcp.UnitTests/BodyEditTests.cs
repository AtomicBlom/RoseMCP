namespace RoseMcp.UnitTests;

/// <summary>
/// The anchored half of a body edit, which is the payload an agent reaches for when re-emitting
/// sixty lines to change one is too expensive. Matching is on the token stream, and everything here
/// turns on what that does and does not see.
/// </summary>
public sealed class BodyEditTests
{
	/// <summary>
	/// A comment is trivia, so the matching cannot see one: an anchor carrying a comment matches on
	/// the code alone, the comment in the file stays where it is, and a comment in the replacement
	/// lands underneath it. Two comments, one of them the one the caller meant to remove, and nothing
	/// in the result says so. Refused instead, naming the payload that can do it.
	/// </summary>
	[Test]
	public void Refuses_an_anchor_carrying_a_comment()
	{
		var error = Assert.Throws<ArgumentException>(
			() => BodyEdit.Anchored("{\n\t// why\n\treturn 1;\n}", "// why\nreturn 1;", "// because\nreturn 2;"));

		Assert.Contains("carries a comment", error.Message, StringComparison.Ordinal);
		// Naming the payload that can do it, which is now the switch that makes the comment matchable
		// rather than only the whole-body rewrite.
		Assert.Contains("includeTrivia", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The other half of the same rule. A comment in the replacement alone is new prose going in
	/// where the code was, which is the only way to comment a body without re-emitting it, and it
	/// duplicates nothing.
	/// </summary>
	[Test]
	public void Takes_a_comment_in_the_replacement()
	{
		var body = BodyEdit.Anchored("{\n\treturn 1;\n}", "return 1;", "// because\nreturn 2;");

		Assert.Contains("// because", body, StringComparison.Ordinal);
		Assert.DoesNotContain("return 1;", body, StringComparison.Ordinal);
	}

	/// <summary>
	/// A comment inside the anchor rather than above it is the same problem: the tokens either side
	/// match, the comment between them does not, and the replacement is spliced across it.
	/// </summary>
	[Test]
	public void Refuses_a_comment_between_the_tokens_of_an_anchor()
	{
		var error = Assert.Throws<ArgumentException>(
			() => BodyEdit.Anchored("{\n\tvar x = 1;\n\treturn x;\n}", "var x = 1; // one\nreturn x;", "return 2;"));

		Assert.Contains("carries a comment", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A replacement whose continuation lines were written for the destination gets the destination's
	/// indentation put on top of them, twice over. The first line of a spliced fragment is written
	/// flush, because the splice point already carries the indentation of the line it lands on -- so
	/// reading the baseline from that line finds nothing to take off, and every wrapped line keeps the
	/// level it already had and gains another. Silently: a continuation line is not a statement, so
	/// Roslyn's formatter has no rule that moves one back and neither IDE0055 nor dotnet format has an
	/// opinion about where a wrapped argument list sits.
	/// </summary>
	[Test]
	public void Does_not_stack_the_destination_indent_on_a_replacement_written_for_it()
	{
		var body = BodyEdit.Anchored(
			"{\n\t\t\t\t\tSend(one);\n}",
			"Send(one);",
			"Send(\n\t\t\t\t\t\tone,\n\t\t\t\t\t\ttwo);");

		Assert.Contains("\n\t\t\t\t\t\tone,", body, StringComparison.Ordinal);
		Assert.DoesNotContain("\t\t\t\t\t\t\tone,", body, StringComparison.Ordinal);
	}

	/// <summary>
	/// The other half of the same question, and the one the first fix traded away. A replacement
	/// written flush with its continuation one level in wants that level added to the destination's,
	/// not replaced by it -- so against a destination only one tab deep, a continuation at one tab has
	/// to come out at two. Reading it as written-for-the-destination flattens it onto the line it
	/// continues, which is why a line at exactly the destination's indentation is not evidence.
	/// </summary>
	[Test]
	public void Keeps_a_flush_replacements_own_wrapping_at_a_shallow_destination()
	{
		var body = BodyEdit.Anchored(
			"{\n\tSend(one);\n}",
			"Send(one);",
			"Send(\n\tone,\n\ttwo);");

		Assert.Contains("\n\t\tone,", body, StringComparison.Ordinal);
		Assert.Contains("\n\t\ttwo);", body, StringComparison.Ordinal);
	}

	/// <summary>
	/// A fragment whose first line carries indentation of its own is written against that, whatever
	/// the destination is: the caller chose a baseline and indented everything to it, so the levels
	/// between its lines are what they meant. Taking the destination off instead would put a line one
	/// level in from a two-tab first line at the destination itself.
	/// </summary>
	[Test]
	public void Measures_a_replacement_against_its_own_first_line_when_it_has_one()
	{
		var body = BodyEdit.Anchored(
			"{\n\tSend(one);\n}",
			"Send(one);",
			"\t\tSend(\n\t\t\tone,\n\t\t\ttwo);");

		Assert.Contains("\n\t\tone,", body, StringComparison.Ordinal);
		Assert.DoesNotContain("\n\t\t\tone,", body, StringComparison.Ordinal);
	}

	/// <summary>
	/// The text path matches exactly, endings included, because inside a comment or a literal an
	/// ending is the content being edited. That is right and it made the path unreachable: every file
	/// here is CRLF and C# composed for a JSON argument is LF, so an anchor spanning two lines never
	/// matched and the refusal named a difference the caller could not see.
	/// <para>
	/// A needle whose every ending is a bare LF is normalised to the body's, which is the same rule
	/// every other supplied payload goes through -- the LF is an artefact of composing a string, not a
	/// decision.
	/// </para>
	/// </summary>
	[Test]
	public void Matches_a_line_feed_needle_against_a_carriage_return_body()
	{
		var rewritten = 0;

		var body = BodyEdit.Anchored(
			"{\r\n\tvar text = \"\"\"\r\n\t\tfirst\r\n\t\tsecond\r\n\t\t\"\"\";\r\n}",
			"first\n\t\tsecond",
			"first\n\t\tthird",
			includeTrivia: true,
			count => rewritten = count);

		Assert.Contains("\t\tfirst\r\n\t\tthird\r\n", body, StringComparison.Ordinal);
		Assert.DoesNotContain("second", body, StringComparison.Ordinal);
		Assert.Equal(1, rewritten);
	}

	/// <summary>
	/// The case normalising unconditionally would have closed as it opened the other: a literal
	/// written with bare LFs inside a body that is otherwise CRLF. Those endings are the string's
	/// value, which is why nothing rewrites them and why both rose_format and rose_add_file report
	/// them -- so a caller reaching for this path is more likely than not to be editing one. The
	/// needle matches as written, and the replacement is spliced as written with it.
	/// </summary>
	[Test]
	public void Reaches_a_line_feed_literal_inside_a_carriage_return_body()
	{
		var rewritten = 0;

		var body = BodyEdit.Anchored(
			"{\r\n\tvar text = \"\"\"\r\n\t\tfirst\nsecond\n\t\t\"\"\";\r\n}",
			"first\nsecond",
			"first\nthird",
			includeTrivia: true,
			count => rewritten = count);

		Assert.Contains("first\nthird\n", body, StringComparison.Ordinal);
		Assert.DoesNotContain("second", body, StringComparison.Ordinal);

		// Nothing was rewritten, so nothing is claimed: the caller's endings were taken literally.
		Assert.Equal(0, rewritten);
	}

	/// <summary>
	/// A needle carrying a carriage return is left exactly as written, which is how to reach an ending
	/// the file does not use -- the same escape hatch as every other payload, and the reason the rule
	/// can be a condition rather than an argument.
	/// </summary>
	[Test]
	public void Leaves_a_needle_that_carries_a_carriage_return_alone()
	{
		var thrown = Assert.Throws<ArgumentException>(() => BodyEdit.Anchored(
			"{\r\n\t// one\n\t// two\r\n}",
			"// one\r\n\t// two",
			"// three",
			includeTrivia: true));

		Assert.Contains("does not contain", thrown.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The words inside a block comment, which is what this path exists for and what the token stream
	/// cannot see at all. One trivia node, so the ending between its lines is content rather than the
	/// gap between two comments -- an anchor spanning two <c>//</c> comments covers the second one's
	/// delimiter and is refused, which is the straddle guard doing its job.
	/// </summary>
	[Test]
	public void Reaches_the_text_inside_a_block_comment()
	{
		var body = BodyEdit.Anchored(
			"{\r\n\t/* counts what is\r\n\t   there */\r\n\treturn 1;\r\n}",
			"counts what is\n\t   there",
			"counts what was\n\t   asked for",
			includeTrivia: true);

		Assert.Contains("/* counts what was\r\n\t   asked for */", body, StringComparison.Ordinal);
	}

	/// <summary>
	/// A replacement whose lines are written partly flush and partly at the destination's own
	/// indentation has no reading that is right for all of them, and the fragment rule correctly
	/// declines to call it written-for-the-destination -- so the flush reading is used and every line
	/// already carrying the destination gains it a second time. Silently, because a continuation line
	/// is not a statement: Roslyn's formatter has no rule that moves one back, and neither IDE0055 nor
	/// dotnet format has an opinion about where a wrapped argument list sits.
	/// <para>
	/// So it is said. Applied as before, because there is no better reading to switch to and refusing
	/// would block a payload nobody can rewrite without being told what is wrong with it, and the
	/// sentence names the lines on each side.
	/// </para>
	/// </summary>
	[Test]
	public void Says_when_a_replacement_mixes_flush_and_destination_indentation()
	{
		var notices = new List<string>();

		BodyEdit.Anchored(
			"{\n\t\t\tSend(one);\n}",
			"Send(one);",
			"var q = source\n\t\t\t.Where(x => x)\n\t\t\t.ToList();\n_ = q;",
			mixed: notices.Add);

		var notice = Assert.Single(notices);

		Assert.Contains("mixes", notice, StringComparison.Ordinal);
		Assert.Contains("line 4", notice, StringComparison.Ordinal);
		Assert.Contains("lines 2, 3", notice, StringComparison.Ordinal);
	}

	/// <summary>
	/// The shape the notice must not fire on: a fragment written flush whose own nesting happens to
	/// reach the destination's depth. Every level between column zero and the destination is present,
	/// which is what a nested block written flush looks like and what a fragment jumping straight from
	/// column zero to the destination's indentation does not have.
	/// </summary>
	[Test]
	public void Says_nothing_about_a_flush_replacement_whose_nesting_reaches_the_destination()
	{
		var notices = new List<string>();

		BodyEdit.Anchored(
			"{\n\t\tSend(one);\n}",
			"Send(one);",
			"if (a)\n{\n\tif (b)\n\t{\n\t\tC();\n\t}\n}",
			mixed: notices.Add);

		Assert.Empty(notices);
	}
}
