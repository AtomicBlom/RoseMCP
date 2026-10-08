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

	private static string Replaced(string comment, string text) =>
		DocComment.Replace(SyntaxFactory.ParseLeadingTrivia(comment + "\t"), text, "\t", "\r\n").ToFullString();
}
