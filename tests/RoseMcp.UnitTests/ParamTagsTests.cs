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
	[Test]
	public void Writes_a_new_tag_after_the_last_real_one_rather_than_into_the_summary()
	{
		var updated = Update(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["loud"]);

		updated.ShouldBe(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud"></param>
			""");
	}

	/// <summary>
	/// The same confusion from the other side: a parameter the summary happens to mention is not a
	/// parameter that has a tag, and skipping it leaves CS1573 behind.
	/// </summary>
	[Test]
	public void Counts_a_paramref_as_no_tag_at_all()
	{
		var updated = Update(
			"""
			/// <summary>Louder when <paramref name="loud"/> is set.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["loud"]);

		updated.ShouldContain("""<param name="loud"></param>""", Case.Sensitive);
	}

	/// <summary>
	/// And the worst of the three: removing a parameter the summary mentions used to delete the whole
	/// line the mention was on, which is a sentence nobody asked to lose and nothing in the diff
	/// explains.
	/// </summary>
	[Test]
	public void Leaves_the_summary_alone_when_the_parameter_it_mentions_goes()
	{
		var updated = Update(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""",
			removed: ["name"]);

		updated.ShouldBe(
			"""
			/// <summary>The greeting for <paramref name="name"/>.</summary>
			/// <param name="loud">Whether to shout.</param>
			""");
	}

	/// <summary>
	/// A member whose only tag has just been removed, which is what renaming a parameter looks like
	/// from here. With nothing left to anchor on the new tag was never written at all, and the build
	/// failed on CS1573 -- so the summary's closing line is the anchor of last resort.
	/// </summary>
	[Test]
	public void Writes_after_the_summary_when_the_removal_took_the_last_tag()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			removed: ["name"],
			added: ["greeting"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="greeting"></param>
			""");
	}

	/// <summary>
	/// Tags written in an order the declaration does not use. They stay in that order, because
	/// reordering documentation nobody asked to reorder is a diff to read for nothing, and the new tag
	/// goes after the tag of the parameter before it, wherever that tag sits -- here in the middle,
	/// not after the last tag.
	/// </summary>
	[Test]
	public void Writes_beside_its_neighbours_tag_where_the_tags_are_out_of_order()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="loud">Whether to shout.</param>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["title"],
			order: ["name", "loud", "title"],
			kept: ["name", "loud"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="loud">Whether to shout.</param>
			/// <param name="title"></param>
			/// <param name="name">Who to greet.</param>
			""");
	}

	/// <summary>
	/// A parameter added between two documented ones, ahead of a trailing cancellation token. Its tag
	/// goes between theirs, in parameter order, rather than after the last tag, where it would read
	/// as documenting the last parameter.
	/// </summary>
	[Test]
	public void Writes_a_tag_for_a_parameter_added_in_the_middle_between_its_neighbours_tags()
	{
		var updated = Update(
			"""
			/// <summary>Ensures each namespace is imported.</summary>
			/// <param name="namespaces">Namespaces to ensure, each already known to have one answer.</param>
			/// <param name="cancellationToken">Cancels the scope lookups.</param>
			""",
			added: ["rules"],
			order: ["namespaces", "rules", "cancellationToken"],
			kept: ["namespaces", "cancellationToken"]);

		updated.ShouldBe(
			"""
			/// <summary>Ensures each namespace is imported.</summary>
			/// <param name="namespaces">Namespaces to ensure, each already known to have one answer.</param>
			/// <param name="rules"></param>
			/// <param name="cancellationToken">Cancels the scope lookups.</param>
			""");
	}

	/// <summary>
	/// A new first parameter has no parameter before it, so its tag goes in front of the tag of the
	/// parameter after it, not after the last one.
	/// </summary>
	[Test]
	public void Writes_a_tag_for_a_new_first_parameter_before_the_tag_of_the_one_after_it()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""",
			added: ["title"],
			order: ["title", "name", "loud"],
			kept: ["name", "loud"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="title"></param>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""");
	}

	/// <summary>
	/// The parameter right before the new one has no tag, so the tag goes after the nearest one before
	/// it that has, rather than falling all the way through to after the last tag.
	/// </summary>
	[Test]
	public void Passes_over_an_undocumented_neighbour_to_the_nearest_documented_one_before()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""",
			added: ["title"],
			order: ["name", "count", "title", "loud"],
			kept: ["name", "count", "loud"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="title"></param>
			/// <param name="loud">Whether to shout.</param>
			""");
	}

	/// <summary>
	/// The same from the other side: with nothing documented before it, the new tag goes in front of
	/// the nearest documented parameter after it, passing over one that has no tag.
	/// </summary>
	[Test]
	public void Passes_over_an_undocumented_neighbour_to_the_nearest_documented_one_after()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""",
			added: ["title"],
			order: ["title", "count", "name", "loud"],
			kept: ["count", "name", "loud"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="title"></param>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""");
	}

	/// <summary>
	/// Two new parameters side by side come out side by side and in parameter order, whatever order
	/// they were asked for in: the second finds the first one's new tag as its neighbour.
	/// </summary>
	[Test]
	public void Writes_tags_for_two_adjacent_new_parameters_in_parameter_order()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			""",
			added: ["count", "title"],
			order: ["name", "title", "count", "loud"],
			kept: ["name", "loud"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="title"></param>
			/// <param name="count"></param>
			/// <param name="loud">Whether to shout.</param>
			""");
	}

	/// <summary>
	/// Two new parameters at the front: the first goes before the tag of the first documented
	/// parameter, and the second after the first's new tag rather than before that same tag, which
	/// would put the two in the wrong order.
	/// </summary>
	[Test]
	public void Writes_tags_for_two_new_leading_parameters_in_parameter_order()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			""",
			added: ["count", "title"],
			order: ["title", "count", "name"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="title"></param>
			/// <param name="count"></param>
			/// <param name="name">Who to greet.</param>
			""");
	}

	/// <summary>
	/// A parameter whose predecessor's tag runs over several lines goes after the line that tag closes
	/// on, not inside its prose, even where another tag follows.
	/// </summary>
	[Test]
	public void Writes_after_the_whole_of_a_predecessors_tag_that_runs_over_several_lines()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">
			/// Who to greet, at enough length that the description wraps onto a line of its own.
			/// </param>
			/// <param name="loud">Whether to shout.</param>
			""",
			added: ["title"],
			order: ["name", "title", "loud"],
			kept: ["name", "loud"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">
			/// Who to greet, at enough length that the description wraps onto a line of its own.
			/// </param>
			/// <param name="title"></param>
			/// <param name="loud">Whether to shout.</param>
			""");
	}

	/// <summary>
	/// A new tag that goes before a successor's tag running over several lines goes before the line it
	/// opens on, so the successor's description stays whole.
	/// </summary>
	[Test]
	public void Writes_before_the_opening_line_of_a_successors_tag_that_runs_over_several_lines()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet, at enough length that the description
			/// wraps onto a second line.</param>
			""",
			added: ["title"],
			order: ["title", "name"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="title"></param>
			/// <param name="name">Who to greet, at enough length that the description
			/// wraps onto a second line.</param>
			""");
	}

	/// <summary>
	/// A tag written in front of another takes that tag's indentation, its marker and the comment's
	/// line ending, and leaves the rest of the trivia exactly as it was.
	/// </summary>
	[Test]
	public void Keeps_the_indentation_and_the_ending_when_it_writes_before_a_tag()
	{
		var updated = ParamTags.Update(
			SyntaxFactory.ParseLeadingTrivia(
				"\t\t/// <summary>Says hello.</summary>\r\n\t\t/// <param name=\"name\">Who to greet.</param>\r\n\t\t"),
			[],
			["loud"],
			["name"],
			["loud", "name"],
			[]);

		updated.ShouldNotBeNull();
		updated.Value.ToFullString().ShouldBe(
			"\t\t/// <summary>Says hello.</summary>\r\n"
				+ "\t\t/// <param name=\"loud\"></param>\r\n"
				+ "\t\t/// <param name=\"name\">Who to greet.</param>\r\n"
				+ "\t\t");
	}

	/// <summary>
	/// Renaming a parameter in the middle of a list takes its tag out and writes the new name's tag
	/// where the old one was, between the tags of its neighbours.
	/// </summary>
	[Test]
	public void Writes_a_renamed_parameters_tag_between_its_neighbours_tags()
	{
		var updated = Update(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="loud">Whether to shout.</param>
			/// <param name="title">How to address them.</param>
			""",
			removed: ["loud"],
			added: ["volume"],
			order: ["name", "volume", "title"],
			kept: ["name", "title"]);

		updated.ShouldBe(
			"""
			/// <summary>Says hello.</summary>
			/// <param name="name">Who to greet.</param>
			/// <param name="volume"></param>
			/// <param name="title">How to address them.</param>
			""");
	}

	/// <summary>
	/// A documented member that took nothing and now takes several parameters gets a tag for each,
	/// after the summary and in parameter order, whatever order they were asked for in.
	/// </summary>
	[Test]
	public void Writes_tags_for_several_first_parameters_of_a_documented_member_in_order()
	{
		var updated = ParamTags.Update(
			SyntaxFactory.ParseLeadingTrivia("\t/// <summary>Lets go of the process.</summary>\r\n\t"),
			[],
			["reason", "failure"],
			[],
			["failure", "reason"],
			[]);

		updated.ShouldNotBeNull();
		updated.Value.ToFullString().ShouldBe(
			"\t/// <summary>Lets go of the process.</summary>\r\n"
				+ "\t/// <param name=\"failure\"></param>\r\n"
				+ "\t/// <param name=\"reason\"></param>\r\n"
				+ "\t");
	}

	/// <summary>
	/// A tag whose description runs over several lines. It opens on one line and closes on a later
	/// one, so the new tag goes after the whole of it -- not after the line it opens on, which put the
	/// new tag inside its prose and, taking its pattern from the line it landed after, copied that
	/// line's words into itself.
	/// <para>
	/// The same failure a <c>paramref</c> in the summary used to cause, arriving from a tag that is
	/// real. Found on this repository's own <c>MemberSyntax.Parse</c>, whose <c>copied</c> tag has
	/// five lines of description.
	/// </para>
	/// </summary>
	[Test]
	public void Writes_after_the_whole_of_a_tag_that_runs_over_several_lines()
	{
		var updated = Update(
			"""
				/// <summary>Says hello.</summary>
				/// <param name="name">
				/// Who to greet. Long enough that whoever wrote it wrapped the description onto a second
				/// line, which is the ordinary shape for anything worth documenting.
				/// </param>
				""",
			added: ["loud"]);

		updated.ShouldBe(
			"""
				/// <summary>Says hello.</summary>
				/// <param name="name">
				/// Who to greet. Long enough that whoever wrote it wrapped the description onto a second
				/// line, which is the ordinary shape for anything worth documenting.
				/// </param>
				/// <param name="loud"></param>
				""");
	}

	/// <summary>
	/// A member that documents none of the parameters it keeps is left alone: a tag for the new one
	/// alone would raise CS1573 for each of the others, and a summary mentioning a parameter is still
	/// not a tag.
	/// </summary>
	[Test]
	public void Leaves_a_member_that_documents_none_of_the_parameters_it_keeps_alone()
	{
		ParamTags.Update(
			SyntaxFactory.ParseLeadingTrivia("""/// <summary>Greets <paramref name="name"/>.</summary>"""),
			[],
			["loud"],
			["name"],
			[],
			[]).ShouldBeNull();
	}

	/// <summary>
	/// A documented member that took nothing and now takes something gets a tag for each new
	/// parameter, after the summary. Its comment then describes every parameter it has, and the
	/// result says the tag needs a description.
	/// </summary>
	[Test]
	public void Writes_a_tag_for_the_first_parameter_of_a_documented_member()
	{
		var notes = new List<string>();

		var updated = ParamTags.Update(
			SyntaxFactory.ParseLeadingTrivia("\t/// <summary>Lets go of the process.</summary>\r\n\t"),
			[],
			["failure"],
			[],
			[],
			notes);

		updated.ShouldNotBeNull();
		updated.Value.ToFullString().ShouldContain(
			"\t/// <summary>Lets go of the process.</summary>\r\n\t/// <param name=\"failure\"></param>",
			Case.Sensitive);
		notes.ShouldContain(note => note.Contains("failure", StringComparison.Ordinal));
	}

	/// <summary>An undocumented member stays undocumented: there is no comment to add a tag to.</summary>
	[Test]
	public void Writes_no_tag_into_a_member_with_no_documentation()
	{
		ParamTags.Update(SyntaxFactory.ParseLeadingTrivia("\t// A note.\r\n\t"), [], ["failure"], [], [], []).ShouldBeNull();
	}

	/// <summary>
	/// The new tag takes its indentation, its marker and its line ending from the line it is modelled
	/// on, so a member nested two levels in does not get a tag at column zero with the wrong ending.
	/// </summary>
	[Test]
	public void Copies_the_indentation_and_the_ending_of_the_line_it_models()
	{
		var updated = Update(
			"\t\t/// <summary>Says hello.</summary>\r\n\t\t/// <param name=\"name\">Who to greet.</param>\r\n",
			added: ["loud"]);

		updated.ShouldEndWith("\r\n\t\t/// <param name=\"loud\"></param>", Case.Sensitive);
	}

	/// <summary>
	/// The tags as they read afterwards, or the comment unchanged when nothing was needed, with the
	/// ends trimmed. The parameters kept default to <c>name</c>, and the order the declaration ends up
	/// with defaults to those followed by the ones added.
	/// <para>
	/// Trimmed because a declaration's leading trivia runs on past its last tag -- to the line break
	/// and the indentation in front of the declaration itself -- and a fixture written as a literal
	/// stops at the last tag. What that costs is the ending of whatever line ends up last, which is
	/// the one thing these cases are not about and one of them is entirely about.
	/// </para>
	/// </summary>
	private static string Update(
		string comment,
		string[]? removed = null,
		string[]? added = null,
		string[]? order = null,
		string[]? kept = null)
	{
		var leading = SyntaxFactory.ParseLeadingTrivia(comment);
		string[] keptHere = kept ?? ["name"];
		string[] orderHere = order ?? [.. keptHere, .. added ?? []];

		return (ParamTags.Update(leading, removed ?? [], added ?? [], keptHere, orderHere, []) ?? leading).ToFullString().TrimEnd();
	}
}
