using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.UnitTests;

/// <summary>
/// A summary passed as plain text, laid out the way the repository writes one. Plain text is the
/// ordinary payload, and written as one line however long it is or however the comment it replaces was
/// laid out, a two-word change to a wrapped summary turns it into a single line past the width of
/// everything around it.
/// </summary>
public sealed class DocCommentTests
{
	[Test]
	public void Keeps_a_short_summary_on_one_line()
	{
		Replaced(string.Empty, "Short.").ShouldBe("\t/// <summary>Short.</summary>\r\n\t");
	}

	[Test]
	public void Keeps_the_lines_the_caller_broke_the_text_over()
	{
		Replaced(string.Empty, "First line.\nSecond line.").ShouldBe(
			"\t/// <summary>\r\n\t/// First line.\r\n\t/// Second line.\r\n\t/// </summary>\r\n\t");
	}

	/// <summary>A replaced summary written as a block stays one, however short its new text.</summary>
	[Test]
	public void Keeps_a_block_summary_a_block()
	{
		Replaced("\t/// <summary>\r\n\t/// Old text.\r\n\t/// </summary>\r\n", "New.").ShouldBe(
			"\t/// <summary>\r\n\t/// New.\r\n\t/// </summary>\r\n\t");
	}

	/// <summary>
	/// A line too long for the width is broken between words, and a tag with a space in it is one word:
	/// a cref split after "see" is two lines of broken XML.
	/// </summary>
	[Test]
	public void Wraps_a_long_line_without_splitting_a_tag()
	{
		var text = string.Join(" ", Enumerable.Repeat("word", 22)) + " <see cref=\"Target\"/> " + string.Join(" ", Enumerable.Repeat("more", 10));

		var written = Replaced(string.Empty, text);
		var lines = written.Split("\r\n");

		lines[0].ShouldBe("\t/// <summary>");
		lines.ShouldAllBe(line => line.Replace("\t", "    ").Length <= 104);
		lines.ShouldContain(line => line.Contains("<see cref=\"Target\"/>"));
		written.Replace("\r\n\t/// ", " ").ShouldContain(text);
	}

	/// <summary>
	/// The width a comment being replaced already ran to is the width it is rewritten at, so a wider
	/// comment is not narrowed by the rewrite.
	/// </summary>
	[Test]
	public void Wraps_at_the_width_the_old_comment_ran_to()
	{
		var wide = "\t/// " + new string('x', 140) + "\r\n";
		var text = string.Join(" ", Enumerable.Repeat("word", 30));

		var lines = Replaced("\t/// <summary>\r\n" + wide + "\t/// </summary>\r\n", text).Split("\r\n");

		lines.ShouldContain(line => line.Replace("\t", "    ").Length > 104);
	}

	/// <summary>
	/// A comment copied out of a file with its markers still on is refused rather than wrapped: taken as
	/// plain text it would be written as a summary holding the whole comment, every line behind a
	/// second marker, and the old documentation replaced by it.
	/// </summary>
	[Test]
	[Arguments("/// <summary>A probe comment.</summary>")]
	[Arguments("/// <summary>\r\n/// A probe comment.\r\n/// </summary>\r\n/// <param name=\"body\">The body.</param>")]
	[Arguments("<summary>\n\t/// A probe comment.\n\t/// </summary>")]
	[Arguments("/** <summary>A probe comment.</summary> */")]
	public void Refuses_a_comment_that_carries_its_own_markers(string comment)
	{
		var error = Should.Throw<ArgumentException>(() => DocComment.Guard(comment));

		error.Message.ShouldContain("carries its own /// or /** markers", Case.Sensitive);
	}

	/// <summary>
	/// Plain text followed by a summary tag would be wrapped in a summary of its own and nest the
	/// tag inside it.
	/// </summary>
	[Test]
	public void Refuses_plain_text_followed_by_a_summary_tag()
	{
		var error = Should.Throw<ArgumentException>(
			() => DocComment.Guard("A probe comment.\n<summary>The real one.</summary>"));

		error.Message.ShouldContain("<summary> tag further on", Case.Sensitive);
	}

	[Test]
	[Arguments("<summary>ok</summary>")]
	[Arguments("<summary>\n\tok\n</summary>\n<param name=\"body\">The body.</param>")]
	[Arguments("A plain sentence.")]
	[Arguments("A plain sentence naming <see cref=\"X\"/> and <c>code</c>.")]
	[Arguments("Reads the file at D:/a///b, which a marker inside a line does not refuse.")]
	public void Accepts_a_comment_without_markers(string comment)
	{
		Should.NotThrow(() => DocComment.Guard(comment));
	}

	private static string Replaced(string comment, string text) =>
		DocComment.Replace(SyntaxFactory.ParseLeadingTrivia(comment + "\t"), text, "\t", "\r\n").ToFullString();
}
