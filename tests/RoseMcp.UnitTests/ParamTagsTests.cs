using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.UnitTests;

/// <summary>
/// Keeping a documentation comment's param tags in step with a changed parameter list.
/// <para>
/// Not a tidiness pass, and not a safe place to be approximately right: where a project generates
/// its documentation file a tag for a parameter that has gone is CS1572 and a parameter with no tag
/// is CS1573, and this repository fails the build on both. A tag written into the wrong place fails
/// the same way, with the added charm of having rewritten somebody's prose to do it.
/// </para>
/// </summary>
public sealed class ParamTagsTests
{
	/// <summary>
	/// A summary that mentions a parameter with <c>paramref</c>. It is prose inside another tag, and
	/// the new tag goes after the last real one -- not after the sentence, on the sentence's own
	/// pattern, which wrote the sentence out a second time and put a param tag inside the summary.
	/// </summary>
	[Fact]
	public void Writes_a_new_tag_after_the_last_real_one_rather_than_into_the_summary()
	{
		var updated = Update(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["loud"]);

		Assert.Equal(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud"></param>
			""",
			updated);
	}

	/// <summary>
	/// The same confusion from the other side: a parameter the summary happens to mention is not a
	/// parameter that has a tag, and skipping it leaves CS1573 behind.
	/// </summary>
	[Fact]
	public void Counts_a_paramref_as_no_tag_at_all()
	{
		var updated = Update(
			"""
			/// <summary>Louder when <paramref name="loud"/> is set.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["loud"]);

		Assert.Contains("""<param name="loud"></param>""", updated, StringComparison.Ordinal);
	}

	/// <summary>
	/// And the worst of the three: removing a parameter the summary mentions used to delete the whole
	/// line the mention was on, which is a sentence nobody asked to lose and nothing in the diff
	/// explains.
	/// </summary>
	[Fact]
	public void Leaves_the_summary_alone_when_the_parameter_it_mentions_goes()
	{
		var updated = Update(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""",
			removed: ["name"]);

		Assert.Equal(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="loud">Whether to shout.</param>
			""",
			updated);
	}

	/// <summary>
	/// A member whose only tag has just been removed, which is what renaming a parameter looks like
	/// from here. With nothing left to anchor on the new tag was never written at all, and the build
	/// failed on CS1573 -- so the summary's closing line is the anchor of last resort.
	/// </summary>
	[Fact]
	public void Writes_after_the_summary_when_the_removal_took_the_last_tag()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			removed: ["name"],
			added: ["greeting"]);

		Assert.Equal(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="greeting"></param>
			""",
			updated);
	}

	/// <summary>
	/// Tags written in an order the declaration does not use. The new one goes after the last of
	/// them, because reordering documentation nobody asked to reorder is a diff to read for nothing.
	/// </summary>
	[Fact]
	public void Anchors_on_the_last_tag_even_where_the_tags_are_out_of_order()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="loud">Whether to shout.</param>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["title"]);

		Assert.Equal(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="loud">Whether to shout.</param>
			/// <param name="name">Who to greet.</param>
			/// <param name="title"></param>
			""",
			updated);
	}

	/// <summary>
	/// A member that documents no parameter is left entirely alone: neither diagnostic fires on one,
	/// and a summary mentioning a parameter is still not a tag.
	/// </summary>
	[Fact]
	public void Leaves_a_member_that_documents_no_parameter_alone()
	{
		Assert.Null(ParamTags.Update(
			SyntaxFactory.ParseLeadingTrivia("""/// <summary>Greets <paramref name="name"/>.</summary>"""),
			[],
			["loud"],
			[]));
	}

	/// <summary>
	/// The new tag takes its indentation, its marker and its line ending from the line it is modelled
	/// on, so a member nested two levels in does not get a tag at column zero with the wrong ending.
	/// </summary>
	[Fact]
	public void Copies_the_indentation_and_the_ending_of_the_line_it_models()
	{
		var updated = Update(
			"\t\t/// <summary>Says hello.</summary>\r\n\t\t/// <param name=\"name\">Who to greet.</param>\r\n",
			added: ["loud"]);

		Assert.EndsWith("\r\n\t\t/// <param name=\"loud\"></param>", updated, StringComparison.Ordinal);
	}

	/// <summary>
	/// The tags as they read afterwards, or the comment unchanged when nothing was needed, with the
	/// ends trimmed.
	/// <para>
	/// Trimmed because a declaration's leading trivia runs on past its last tag -- to the line break
	/// and the indentation in front of the declaration itself -- and a fixture written as a literal
	/// stops at the last tag. What that costs is the ending of whatever line ends up last, which is
	/// the one thing these cases are not about and one of them is entirely about.
	/// </para>
	/// </summary>
	private static string Update(string comment, string[]? removed = null, string[]? added = null)
	{
		var leading = SyntaxFactory.ParseLeadingTrivia(comment);

		return (ParamTags.Update(leading, removed ?? [], added ?? [], []) ?? leading).ToFullString().TrimEnd();
	}
}
