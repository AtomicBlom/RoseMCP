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
}
