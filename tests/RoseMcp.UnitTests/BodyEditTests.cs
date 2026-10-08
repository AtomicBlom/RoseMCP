using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
		var error = Should.Throw<ArgumentException>(
			() => BodyEdit.Anchored("{\n\t// why\n\treturn 1;\n}", "// why\nreturn 1;", "// because\nreturn 2;")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("carries a comment", Case.Sensitive);
		// Naming the payload that can do it, which is now the switch that makes the comment matchable
		// rather than only the whole-body rewrite.
		error.Message.ShouldContain("includeTrivia", Case.Sensitive);
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

		body.ShouldContain("// because", Case.Sensitive);
		body.ShouldNotContain("return 1;", Case.Sensitive);
	}

	/// <summary>
	/// A comment inside the anchor rather than above it is the same problem: the tokens either side
	/// match, the comment between them does not, and the replacement is spliced across it.
	/// </summary>
	[Test]
	public void Refuses_a_comment_between_the_tokens_of_an_anchor()
	{
		var error = Should.Throw<ArgumentException>(
			() => BodyEdit.Anchored("{\n\tvar x = 1;\n\treturn x;\n}", "var x = 1; // one\nreturn x;", "return 2;")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("carries a comment", Case.Sensitive);
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

		body.ShouldContain("\n\t\t\t\t\t\tone,", Case.Sensitive);
		body.ShouldNotContain("\t\t\t\t\t\t\tone,", Case.Sensitive);
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

		body.ShouldContain("\n\t\tone,", Case.Sensitive);
		body.ShouldContain("\n\t\ttwo);", Case.Sensitive);
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

		body.ShouldContain("\n\t\tone,", Case.Sensitive);
		body.ShouldNotContain("\n\t\t\tone,", Case.Sensitive);
	}

	/// <summary>
	/// A replacement copied out of the file, its first line still carrying the indentation it had there,
	/// whose later lines step back out past it -- the brace closing the block above, then a statement
	/// beside it. Each line shallower than the first keeps its distance from it. Adding the destination
	/// to what those lines already had put the brace five levels in, and the continuation lines with it,
	/// which no formatting rule moves back.
	/// </summary>
	[Test]
	public void Keeps_the_lines_a_copied_replacement_steps_back_out_to()
	{
		var body = BodyEdit.Anchored(
			"{\n\t\tif (x)\n\t\t{\n\t\t\tyield return 1\n\t\t\t\t\t: string.Empty;\n\t\t}\n\n\t\treturn;\n\t}",
			"\t\t\t\t\t: string.Empty;\n\t\t}",
			"\t\t\t\t\t: string.Empty;\n\t\t}\n\n\t\tif (y)\n\t\t{\n\t\t\tyield return $\"a \"\n\t\t\t\t+ \"b\";\n\t\t}");

		body.ShouldContain(
			"\t\t\t\t\t: string.Empty;\n\t\t}\n\n\t\tif (y)\n\t\t{\n\t\t\tyield return $\"a \"\n\t\t\t\t+ \"b\";\n\t\t}\n\n\t\treturn;",
			Case.Sensitive);
	}

	/// <summary>
	/// A replacement starting mid-line whose continuation is written at exactly the depth it sits at in
	/// the file, which is what editing a phrase copied out of the file produces. Read as flush, it gained
	/// the destination's indentation on top of its own and landed twice as deep.
	/// </summary>
	[Test]
	public void Leaves_a_continuation_written_at_the_destinations_own_depth()
	{
		var body = BodyEdit.Anchored(
			"{\n\t\t\t\tthrow new X(\n\t\t\t\t$\"once \"\n\t\t\t\t+ \"twice\");\n}",
			"$\"once \"\n+ \"twice\"",
			"$\"one \"\n\t\t\t\t+ \"two\"");

		body.ShouldContain("\n\t\t\t\t$\"one \"\n\t\t\t\t+ \"two\");", Case.Sensitive);
	}

	/// <summary>
	/// An element added after the one matched, at the depth the elements sit at. The same shape as the
	/// continuation above, reached by an insertion rather than a rewording.
	/// </summary>
	[Test]
	public void Inserts_an_element_at_the_depth_of_the_one_it_follows()
	{
		var body = BodyEdit.Anchored(
			"{\n\t\tFirst,\n\t\tSecond,\n}",
			"First,",
			"First,\n\t\tAdded,");

		body.ShouldContain("\n\t\tFirst,\n\t\tAdded,\n\t\tSecond,", Case.Sensitive);
	}

	/// <summary>
	/// A replacement starting mid-line whose later lines close what it opened, one level out from the
	/// line the match started on. Nothing between column zero and that depth appears in it, so it was
	/// written for the destination, not nested from flush.
	/// </summary>
	[Test]
	public void Leaves_the_lines_that_close_a_replacement_one_level_out()
	{
		var body = BodyEdit.Anchored(
			"{\n\t\t\t\tnew Options\n\t\t\t\t{\n\t\t\t\t\tHeaders = null,\n\t\t\t\t}),\n\t\t\t\tdone);\n}",
			"Headers = null,\n}),",
			"Headers = Make(),\n\t\t\t\t}),");

		body.ShouldContain("\n\t\t\t\t\tHeaders = Make(),\n\t\t\t\t}),\n\t\t\t\tdone);", Case.Sensitive);
	}

	/// <summary>
	/// The text path matches exactly, endings included, because inside a comment or a literal an
	/// ending is the content being edited. That is right and it made the path unreachable: every file
	/// here is CRLF and C# composed for a JSON argument is LF, so an anchor spanning two lines never
	/// matched and the refusal named a difference the caller could not see.
	/// <para>
	/// A needle whose every ending is a bare LF is normalised to the body's, which is the same rule
	/// every other supplied payload goes through -- the LF is an artefact of composing a string, not a
	/// decision. The replacement's ending lands inside a literal that opened before it, so it is named
	/// by the replacement's first line, which is where the caller's text is inside it.
	/// </para>
	/// </summary>
	[Test]
	public void Matches_a_line_feed_needle_against_a_carriage_return_body()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var body = BodyEdit.Anchored(
			"{\r\n\tvar text = \"\"\"\r\n\t\tfirst\r\n\t\tsecond\r\n\t\t\"\"\";\r\n}",
			"first\n\t\tsecond",
			"first\n\t\tthird",
			includeTrivia: true,
			literals => rewritten = literals);

		body.ShouldContain("\t\tfirst\r\n\t\tthird\r\n", Case.Sensitive);
		body.ShouldNotContain("second", Case.Sensitive);
		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(0, 1));
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
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var body = BodyEdit.Anchored(
			"{\r\n\tvar text = \"\"\"\r\n\t\tfirst\nsecond\n\t\t\"\"\";\r\n}",
			"first\nsecond",
			"first\nthird",
			includeTrivia: true,
			literals => rewritten = literals);

		body.ShouldContain("first\nthird\n", Case.Sensitive);
		body.ShouldNotContain("second", Case.Sensitive);

		// Nothing was rewritten, so nothing is claimed: the caller's endings were taken literally.
		rewritten.ShouldBeNull();
	}

	/// <summary>
	/// Endings rewritten inside a comment are layout, as they are anywhere outside a literal, so the
	/// replacement takes the body's endings and nothing is said about it.
	/// </summary>
	[Test]
	public void Says_nothing_about_endings_rewritten_inside_a_comment()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var body = BodyEdit.Anchored(
			"{\r\n\t// one\r\n\t// two\r\n\treturn 1;\r\n}",
			"// one\n\t// two",
			"// uno\n\t// dos",
			includeTrivia: true,
			literals => rewritten = literals);

		body.ShouldContain("// uno\r\n\t// dos\r\n", Case.Sensitive);
		rewritten.ShouldBeNull();
	}

	/// <summary>
	/// A replacement that carries a literal of its own names it by the line of the replacement it opens
	/// on, and counts only the endings inside it: the one after the literal is code.
	/// </summary>
	[Test]
	public void Names_a_literal_the_replacement_carries_on_its_own_line()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var body = BodyEdit.Anchored(
			"{\r\n\tvar a = 1;\r\n\treturn a;\r\n}",
			"var a = 1;\n\treturn a;",
			"var a = 1;\n\tvar b = @\"x\ny\";\n\treturn a;",
			includeTrivia: true,
			literals => rewritten = literals);

		body.ShouldContain("@\"x\r\ny\"", Case.Sensitive);
		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(1, 1));
	}

	/// <summary>
	/// A token-matched replacement composed with bare LFs takes the endings of a body written with CR LF
	/// before it meets the body's text, since afterwards the body's own endings would answer for the
	/// caller and the literal would keep its LFs. The literal is named on its line in what was sent,
	/// counted from the blank line above it that the splice drops.
	/// </summary>
	[Test]
	public void Gives_a_token_matched_replacement_the_bodys_endings()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var body = BodyEdit.Anchored(
			"{\r\n\treturn a;\r\n}",
			"a",
			"\na\n\t+ @\"x\ny\"",
			rewritten: literals => rewritten = literals);

		body.ShouldContain("@\"x\r\ny\"", Case.Sensitive);
		body.ShouldNotContain("\n\n", Case.Sensitive);
		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(2, 1));
	}

	/// <summary>
	/// The ending a replacement takes is the one the file's layout writes, not whatever the body was
	/// checked out with: a repository that declares LF keeps the caller's LF literal as it was, and
	/// nothing claims an ending was rewritten.
	/// </summary>
	[Test]
	public void Gives_a_token_matched_replacement_the_layouts_ending_rather_than_the_bodys()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var body = BodyEdit.Anchored(
			"{\r\n\treturn a;\r\n}",
			"a",
			"@\"x\ny\"",
			rewritten: literals => rewritten = literals,
			lineEnding: "\n");

		body.ShouldContain("@\"x\ny\"", Case.Sensitive);
		rewritten.ShouldBeNull();
	}

	/// <summary>
	/// Code inserted with bare LFs into a CRLF block takes the block's ending, and a literal it carries
	/// is named on its line in the code sent, counted from the blank line above it that the insertion
	/// drops.
	/// </summary>
	[Test]
	public void Gives_inserted_code_the_files_ending_and_names_its_literal()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;
		var (declaration, block) = Method("void M()\r\n{\r\n\tvar a = 1;\r\n}");

		var body = BodyEdit.Inserted(
			declaration,
			block,
			"\nLog();\nvar b = @\"x\ny\";",
			atStart: false,
			[],
			out _,
			"\r\n",
			literals => rewritten = literals);

		body.ShouldContain("@\"x\r\ny\"", Case.Sensitive);
		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(2, 1));
	}

	/// <summary>
	/// The statements around an insertion are joined with the block's own ending. Joined with bare LFs,
	/// a CRLF block reads as one nobody had a view about, and the member parsed from it has the bare LF
	/// in a literal of the file's own rewritten by an insertion that never touched it.
	/// </summary>
	[Test]
	public void Joins_the_statements_around_an_insertion_with_the_blocks_ending()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;
		var (declaration, block) = Method("void M()\r\n{\r\n\tvar x = @\"a\nb\";\r\n\treturn;\r\n}");

		var body = BodyEdit.Inserted(
			declaration,
			block,
			"Log();",
			atStart: false,
			[],
			out _,
			"\r\n",
			literals => rewritten = literals);

		body.ShouldBe("var x = @\"a\nb\";\r\n\r\nLog();\r\n\r\nreturn;");
		rewritten.ShouldBeNull();
	}

	private static (MethodDeclarationSyntax Declaration, BlockSyntax Block) Method(string code)
	{
		var declaration = SyntaxFactory.ParseMemberDeclaration(code).ShouldBeOfType<MethodDeclarationSyntax>();

		return (declaration, declaration.Body.ShouldNotBeNull());
	}

	/// <summary>
	/// A needle carrying a carriage return is left exactly as written, which is how to reach an ending
	/// the file does not use -- the same escape hatch as every other payload, and the reason the rule
	/// can be a condition rather than an argument.
	/// </summary>
	[Test]
	public void Leaves_a_needle_that_carries_a_carriage_return_alone()
	{
		var thrown = Should.Throw<ArgumentException>(() => BodyEdit.Anchored(
			"{\r\n\t// one\n\t// two\r\n}",
			"// one\r\n\t// two",
			"// three",
			includeTrivia: true)).ShouldBeOfType<ArgumentException>();

		thrown.Message.ShouldContain("does not contain", Case.Sensitive);
	}

	/// <summary>
	/// The words inside a block comment, which is what this path exists for and what the token stream
	/// cannot see at all. One trivia node, so the ending between its lines is content -- the same as a run
	/// of <c>//</c> lines, which Roslyn makes one trivia per line but a reader takes as one comment.
	/// </summary>
	[Test]
	public void Reaches_the_text_inside_a_block_comment()
	{
		var body = BodyEdit.Anchored(
			"{\r\n\t/* counts what is\r\n\t   there */\r\n\treturn 1;\r\n}",
			"counts what is\n\t   there",
			"counts what was\n\t   asked for",
			includeTrivia: true);

		body.ShouldContain("/* counts what was\r\n\t   asked for */", Case.Sensitive);
	}

	/// <summary>
	/// A comment written as several <c>//</c> lines, reworded across a line. Roslyn makes each line its
	/// own trivia, but the run is one comment, so a match crossing its lines is inside it rather than
	/// across a delimiter -- and this repository's comments are most often exactly that shape.
	/// </summary>
	[Test]
	public void Rewords_a_comment_written_as_several_line_comments()
	{
		var body = BodyEdit.Anchored(
			"{\r\n\t// counts what is\r\n\t// there\r\n\treturn 1;\r\n}",
			"what is\n\t// there",
			"what was\n\t// asked for",
			includeTrivia: true);

		body.ShouldContain("// counts what was\r\n\t// asked for\r\n", Case.Sensitive);
	}

	/// <summary>
	/// What makes that safe. A match crossing the lines of a <c>//</c> comment crosses their delimiters,
	/// so a replacement that leaves one out would turn the comment's words into code.
	/// </summary>
	[Test]
	public void Refuses_a_replacement_that_leaves_a_comment_line_without_its_delimiter()
	{
		var thrown = Should.Throw<ArgumentException>(() => BodyEdit.Anchored(
			"{\r\n\t// counts what is\r\n\t// there\r\n\treturn 1;\r\n}",
			"what is\n\t// there",
			"what was\n\tasked for",
			includeTrivia: true)).ShouldBeOfType<ArgumentException>();

		thrown.Message.ShouldContain("'asked for' does not", Case.Sensitive);
	}

	/// <summary>
	/// A run ends where the comment does. A match from its last line into the code after it still
	/// straddles, and so does one across a blank line, which separates two comments for a reader too.
	/// </summary>
	[Test]
	[Arguments("{\r\n\t// one\r\n\t// two\r\n\treturn 1;\r\n}", "two\n\treturn")]
	[Arguments("{\r\n\t// one\r\n\r\n\t// two\r\n\treturn 1;\r\n}", "one\n\n\t// two")]
	public void Still_refuses_a_match_that_leaves_the_comment(string body, string find)
	{
		var thrown = Should.Throw<ArgumentException>(() => BodyEdit.Anchored(body, find, "x", includeTrivia: true)).ShouldBeOfType<ArgumentException>();

		thrown.Message.ShouldContain("part of a comment and part of the code", Case.Sensitive);
	}

	/// <summary>
	/// A match that takes whole comments along with the code under them cuts no delimiter, whatever it
	/// crosses: every one it replaces is in find. It is how a comment and the statement it describes are
	/// rewritten together, which is the commonest reason to touch a comment at all -- a single line, a
	/// run of them, or a run from its second line.
	/// </summary>
	[Test]
	[Arguments(
		"{\r\n\t// Takes one.\r\n\tTake(1);\r\n\treturn;\r\n}",
		"// Takes one.\n\tTake(1);",
		"// Takes two.\n\tTake(2);",
		"{\r\n\t// Takes two.\r\n\tTake(2);\r\n\treturn;\r\n}")]
	[Arguments(
		"{\r\n\t// Rule one\r\n\t// and two.\r\n\tTake(1);\r\n\treturn;\r\n}",
		"// Rule one\n\t// and two.\n\tTake(1);",
		"// Rule three.\n\tTake(3);",
		"{\r\n\t// Rule three.\r\n\tTake(3);\r\n\treturn;\r\n}")]
	[Arguments(
		"{\r\n\t// Rule one\r\n\t// and two.\r\n\tTake(1);\r\n\treturn;\r\n}",
		"// and two.\n\tTake(1);",
		"Take(2);",
		"{\r\n\t// Rule one\r\n\tTake(2);\r\n\treturn;\r\n}")]
	public void Rewrites_whole_comments_with_the_code_under_them(string body, string find, string replace, string expected)
	{
		BodyEdit.Anchored(body, find, replace, includeTrivia: true).ShouldBe(expected);
	}

	/// <summary>
	/// The same for a literal: taken whole, from its opening quote, with the code after it, is code the
	/// caller wrote out delimiter and all.
	/// </summary>
	[Test]
	public void Rewrites_a_whole_literal_with_the_code_after_it()
	{
		var body = BodyEdit.Anchored("{\r\n\treturn \"total\";\r\n}", "\"total\";", "\"count\";", includeTrivia: true);

		body.ShouldBe("{\r\n\treturn \"count\";\r\n}");
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

		var notice = notices.ShouldHaveSingleItem();

		notice.ShouldContain("mixes", Case.Sensitive);
		notice.ShouldContain("line 4", Case.Sensitive);
		notice.ShouldContain("lines 2, 3", Case.Sensitive);
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

		notices.ShouldBeEmpty();
	}
}
