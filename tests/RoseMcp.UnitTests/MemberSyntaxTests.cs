using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// The parse that happens before anything is written. Everything here is a refusal that costs
/// nothing, standing in for a build that costs twenty seconds and a file left broken until someone
/// pays for it -- and every case below is drawn from a failure that actually reached disk when the
/// same edit went through a text tool.
/// </summary>
public sealed class MemberSyntaxTests
{
	[Test]
	public void Parses_the_members_the_code_declares()
	{
		var members = Parse("public int Count { get; set; }\n\npublic void Reset() => Count = 0;");

		members.Count.ShouldBe(2);
		members[0].ShouldBeOfType<PropertyDeclarationSyntax>();
		members[1].ShouldBeOfType<MethodDeclarationSyntax>();
	}

	/// <summary>
	/// The failure with no error at all. Code that closes the container early and opens something of
	/// its own leaves a file that parses perfectly, with a type nobody asked for at the top level --
	/// so the shape has to be checked rather than only the syntax.
	/// </summary>
	[Test]
	public void Refuses_code_that_would_land_outside_the_member()
	{
		var error = Should.Throw<ArgumentException>(() => Parse("public void M() { } } public class Escaped {")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("closes more braces", Case.Sensitive);
	}

	/// <summary>An unbalanced brace on its own is a parse error, and reported as one.</summary>
	[Test]
	public void Refuses_a_stray_closing_brace()
	{
		var error = Should.Throw<ArgumentException>(() => Parse("public void M()\n{\n}\n}")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("does not parse", Case.Sensitive);
	}

	[Test]
	public void Refuses_a_brace_that_is_never_closed_and_says_where()
	{
		var error = Should.Throw<ArgumentException>(() => Parse("public void M()\n{\n\tif (true)\n\t{\n")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("does not parse", Case.Sensitive);
		error.Message.ShouldContain("line ", Case.Sensitive);
	}

	/// <summary>
	/// The escapes that leaked into source and cost nine compile errors were this: a shell ate one
	/// layer of quoting and what landed was not C#. It is caught here rather than at the build.
	/// </summary>
	[Test]
	public void Refuses_source_with_escapes_left_in_it()
	{
		var error = Should.Throw<ArgumentException>(() => Parse("public string M() => \\$\"{Value}\";")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("does not parse", Case.Sensitive);
	}

	/// <summary>
	/// A using directive inside a member position is a scope mistake rather than a typing one, so
	/// the message says what the tool writes instead of leaving a parser error to be decoded.
	/// </summary>
	[Test]
	public void Says_that_a_using_directive_is_not_a_member()
	{
		var error = Should.Throw<ArgumentException>(() => Parse("using System.Text;\n\npublic void M() { }")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("using directive", Case.Sensitive);
	}

	/// <summary>
	/// A comment after the last member attaches to the closing brace of its container, so it belongs
	/// to no member and would vanish. Silently losing a comment is invisible in a diff nobody reads.
	/// </summary>
	[Test]
	public void Refuses_a_comment_that_would_be_dropped()
	{
		var error = Should.Throw<ArgumentException>(() => Parse("public void M() { }\n\n// and another thing")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("belongs to no member", Case.Sensitive);
	}

	[Test]
	public void Refuses_code_that_declares_nothing()
	{
		Should.Throw<ArgumentException>(() => Parse("// just a comment")).ShouldBeOfType<ArgumentException>();
		Should.Throw<ArgumentException>(() => Parse("   ")).ShouldBeOfType<ArgumentException>();
	}

	/// <summary>
	/// The documentation comment has to arrive attached to the member, or replacing a declaration
	/// would drop the documentation the caller wrote for it.
	/// </summary>
	[Test]
	public void Keeps_a_documentation_comment_with_the_member_it_describes()
	{
		var members = Parse("/// <summary>Counts.</summary>\npublic int Count { get; set; }");

		members.ShouldHaveSingleItem();
		members[0].GetLeadingTrivia().Any(MemberSyntax.IsComment).ShouldBeTrue();
	}

	/// <summary>
	/// Parsed in a container of the right kind, because a member is only meaningful in one. An enum
	/// member is not a declaration anywhere else, and a bodiless method is only ordinary inside an
	/// interface.
	/// </summary>
	[Test]
	[Arguments("enum", "Blue = 3")]
	[Arguments("interface", "double Area();")]
	[Arguments("struct", "public readonly int X;")]
	[Arguments("record", "public int Y { get; init; }")]
	public void Parses_a_member_in_the_container_it_belongs_to(string keyword, string code)
	{
		MemberSyntax.Parse(code, keyword, null).ShouldHaveSingleItem();
	}

	/// <summary>
	/// An enum member parses only against an enum, which is the case the container keyword exists
	/// for: a class wrapper turns the same text into a field with no type and rejects it.
	/// </summary>
	[Test]
	public void Refuses_an_enum_member_offered_to_a_class()
	{
		Should.Throw<ArgumentException>(() => Parse("Blue = 3")).ShouldBeOfType<ArgumentException>();
	}

	[Test]
	[Arguments("class C { }", "class")]
	[Arguments("interface I { }", "interface")]
	[Arguments("enum E { }", "enum")]
	[Arguments("record R { }", "record")]
	[Arguments("record struct S { }", "record struct")]
	public void Reports_the_keyword_a_container_was_declared_with(string declaration, string expected)
	{
		var tree = CSharpSyntaxTree.ParseText(declaration, cancellationToken: TestContext.Current!.Execution.CancellationToken);
		var unit = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current!.Execution.CancellationToken);
		var type = (BaseTypeDeclarationSyntax)unit.Members[0];

		MemberSyntax.KeywordOf(type).ShouldBe(expected);
	}

	/// <summary>
	/// The half of formatting the formatter does not do. It reindents statements and moves braces,
	/// so a line wrapped inside a body comes out right, but a wrapped parameter list is layout it
	/// has no rule about and keeps whatever arrived -- and neither IDE0055 nor dotnet format has an
	/// opinion either, so code written for column zero lands a level short of its neighbours and
	/// nothing complains.
	/// </summary>
	[Test]
	public void Shifts_wrapped_lines_to_the_indentation_of_where_they_are_going()
	{
		var members = MemberSyntax.Parse(
			"public void Write(\n\tint count,\n\tstring name)\n{\n\tSend(count);\n}",
			"class",
			null,
			"\t");

		var text = members.ShouldHaveSingleItem().ToFullString();

		// A member at one tab wraps its parameters at two and holds its body at two.
		text.ShouldContain("\n\t\tint count,", Case.Sensitive);
		text.ShouldContain("\n\t\tstring name)", Case.Sensitive);
		text.ShouldContain("\n\t{", Case.Sensitive);
		text.ShouldContain("\n\t\tSend(count);", Case.Sensitive);
	}

	/// <summary>
	/// A caller that has read the file and indented for the destination is as likely as one that
	/// wrote at column zero, and the two have to be the same request -- which is why the baseline
	/// comes off before the destination's indentation goes on.
	/// </summary>
	[Test]
	public void Treats_code_already_indented_for_its_destination_the_same_way()
	{
		var atColumnZero = MemberSyntax.Parse(
			"public void Write(\n\tint count)\n{\n\tSend(count);\n}", "class", null, "\t");

		var preIndented = MemberSyntax.Parse(
			"\tpublic void Write(\n\t\tint count)\n\t{\n\t\tSend(count);\n\t}", "class", null, "\t");

		preIndented.ShouldHaveSingleItem().ToFullString().ShouldBe(
			atColumnZero.ShouldHaveSingleItem().ToFullString());
	}

	/// <summary>
	/// A declaration arriving as its own source text, indentation and all, which is what a move
	/// hands over. Its first line carries the level it was written at, so that is the baseline, and
	/// every wrapped line keeps the relation it had to it.
	/// </summary>
	[Test]
	public void Measures_a_moved_declaration_against_the_indentation_it_arrives_with()
	{
		var members = MemberSyntax.Parse(
			"\tpublic static string Join(\n\t\tstring first,\n\t\tstring second)\n\t{\n\t\treturn first;\n\t}",
			"class",
			null,
			"\t");

		var text = members.ShouldHaveSingleItem().ToFullString();

		text.ShouldContain("\n\t\tstring first,", Case.Sensitive);
		text.ShouldNotContain("\n\t\t\tstring first,", Case.Sensitive);
	}

	/// <summary>
	/// The line inside a string is content, not layout. Shifting it changes what the program says,
	/// and in a raw literal it changes how much is stripped from every other line of the value.
	/// </summary>
	[Test]
	public void Leaves_the_lines_inside_a_multi_line_literal_where_they_are()
	{
		var members = MemberSyntax.Parse(
			"public string Text() => @\"\nkeep me here\n\";", "class", null, "\t");

		var text = members.ShouldHaveSingleItem().ToFullString();

		text.ShouldContain("\nkeep me here\n", Case.Sensitive);
	}

	private static IReadOnlyList<MemberDeclarationSyntax> Parse(string code) =>
		MemberSyntax.Parse(code, "class", null);

	/// <summary>
	/// A lone LF becomes the destination's ending, inside a raw literal as well as outside one. The
	/// code arrives as a JSON argument, which carries no carriage return, so keeping what arrived keeps
	/// an artefact of the transport -- and produces a file dotnet format rejects while no build says
	/// anything.
	/// <para>
	/// Only the ending between <c>first</c> and <c>second</c> is reported. The other three are layout,
	/// or the two a raw literal drops from its value beside its delimiters.
	/// </para>
	/// </summary>
	[Test]
	public void Rewrites_a_lone_line_feed_to_the_destination_ending()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var members = MemberSyntax.Parse(
			"public const string Text = \"\"\"\nfirst\nsecond\n\"\"\";\n",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		var written = members.ShouldHaveSingleItem().ToFullString();

		written.ShouldContain("\"\"\"\r\n\tfirst\r\n\tsecond\r\n\t\"\"\"", Case.Sensitive);
		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(0, 1));
	}

	/// <summary>
	/// A carriage return the caller wrote deliberately survives, so a file that is otherwise LF can
	/// still be given a CRLF literal.
	/// </summary>
	[Test]
	public void Keeps_a_carriage_return_the_caller_supplied()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		MemberSyntax.Parse(
			"public const string Text = \"\"\"\r\nfirst\r\n\"\"\";\r\n",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\n",
			rewritten: literals => rewritten = literals);

		rewritten.ShouldBeNull();
	}

	/// <summary>
	/// Code holding no multi-line literal still has every ending rewritten, which is what keeps the file
	/// passing dotnet format, and nothing is said about it: outside a literal an ending is layout, and a
	/// sentence on every payload is one a caller learns to skim.
	/// </summary>
	[Test]
	public void Rewrites_the_endings_of_code_without_a_literal_and_says_nothing()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var members = MemberSyntax.Parse(
			"public string Name()\n{\n\tvar text = \"one line\";\n\treturn text;\n}\n",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		var written = members.ShouldHaveSingleItem().ToFullString();

		written.ShouldContain("public string Name()\r\n\t{\r\n", Case.Sensitive);
		written.ShouldContain("\r\n\t\treturn", Case.Sensitive);
		// Trimmed, since the ending after the last line is the parse wrapper's and the member's own
		// preparation gives it the file's.
		written.TrimEnd().Replace("\r\n", string.Empty, StringComparison.Ordinal).ShouldNotContain("\n", Case.Sensitive, "Every ending comes out as the file's.");
		rewritten.ShouldBeNull();
	}

	/// <summary>
	/// A verbatim literal's endings are all its value, and it is named by the line it opens on, counted
	/// the way the caller counts the code they sent.
	/// </summary>
	[Test]
	public void Names_the_line_a_verbatim_literal_opens_on()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		MemberSyntax.Parse(
			"public string Text()\n{\n\tvar count = 1;\n\tvar text = @\"one\ntwo\nthree\";\n\treturn text;\n}\n",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(3, 2));
	}

	/// <summary>
	/// A raw literal holding one line has no ending in its value: the compiler drops the one after its
	/// opening delimiter and the one in front of its closing delimiter's line. Rewriting those changes
	/// nothing the program says, so nothing is said.
	/// </summary>
	[Test]
	public void Says_nothing_about_a_raw_literal_holding_one_line()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var members = MemberSyntax.Parse(
			"public const string Text = \"\"\"\nonly\n\"\"\";\n",
			"class",
			options: null,
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		members.ShouldHaveSingleItem().ToFullString().ShouldContain("\"\"\"\r\nonly\r\n\"\"\"", Case.Sensitive);
		rewritten.ShouldBeNull();
	}

	/// <summary>
	/// An interpolation hole spread over lines is code, so its endings are layout like any other. A
	/// literal whose only endings besides its delimiters' are inside a hole changed no text.
	/// </summary>
	[Test]
	public void Says_nothing_about_endings_inside_an_interpolation_hole()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		MemberSyntax.Parse(
			"public string Text(int x) => $\"\"\"\n\ta {x\n\t\t+ 1} b\n\t\"\"\";\n",
			"class",
			options: null,
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		rewritten.ShouldBeNull();
	}

	/// <summary>The same literal with an ending in its own text is a changed string, and is named.</summary>
	[Test]
	public void Names_an_interpolated_literal_whose_text_holds_an_ending()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		MemberSyntax.Parse(
			"public string Text(int x) => $\"\"\"\n\ta {x\n\t\t+ 1} b\n\tc\n\t\"\"\";\n",
			"class",
			options: null,
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(0, 1));
	}

	/// <summary>
	/// Literals on one line are never named, because they hold no ending; every multi-line one is, each
	/// on its own line.
	/// </summary>
	[Test]
	public void Names_each_multi_line_literal_and_no_single_line_one()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		MemberSyntax.Parse(
			"public string A => \"one line\";\n\npublic string B => @\"one\ntwo\";\n\npublic string C => @\"three\nfour\";\n",
			"class",
			options: null,
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		rewritten.ShouldNotBeNull().ShouldBe(
			[new MemberSyntax.RewrittenLiteral(2, 1), new MemberSyntax.RewrittenLiteral(5, 1)]);

		MemberSyntax.RewrittenEndings(rewritten, "CRLF")
			.ShouldContain("inside the multi-line string literals on lines 3 and 6 of the code supplied", Case.Sensitive);
	}

	/// <summary>
	/// A body replacement joins the copied signature to the caller's body on the signature's last line,
	/// so the ending that line comes out with is the caller's. A verbatim literal opening there takes the
	/// file's ending with every other one in it, rather than keeping its first as it arrived.
	/// </summary>
	[Test]
	public void Rewrites_the_ending_of_the_line_a_body_joins_its_signature_on()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var members = MemberSyntax.Parse(
			"public string Text() => @\"one\ntwo\nthree\";",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals,
			copied: "public string Text()");

		members.ShouldHaveSingleItem().ToFullString().ShouldContain("@\"one\r\ntwo\r\nthree\"", Case.Sensitive);
		rewritten.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(0, 2));
	}

	/// <summary>
	/// The sentence names the literal's line from one and keeps the escape hatch, and says nothing about
	/// what a bare LF is or where one comes from: it is said only where a literal's value changed, and a
	/// caller reading it there needs the line and the way to keep the endings, not an account of how
	/// composing a string produces them.
	/// </summary>
	[Test]
	public void Says_where_the_rewritten_literal_is()
	{
		var notice = MemberSyntax.RewrittenEndings([new MemberSyntax.RewrittenLiteral(3, 2)], "CRLF");

		notice.ShouldBe(
			"Rewrote 2 line ending(s) to CRLF, the ending this file uses, inside the multi-line string literal on "
				+ "line 4 of the code supplied, which changes its value. Write one CR LF anywhere in the code to keep "
				+ "every ending exactly as it arrived.");
	}

	/// <summary>
	/// A literal in a branch of an <c>#if</c> the lexer takes as inactive is still a literal in the build
	/// that defines the symbol, and its endings are rewritten with every other. So both branches' literals
	/// are named, each on its own line, and the inactive one's interior is left where it was, since moving
	/// a verbatim literal's line changes what that build says.
	/// </summary>
	[Test]
	public void Names_a_literal_in_every_branch_of_an_if()
	{
		IReadOnlyList<MemberSyntax.RewrittenLiteral>? rewritten = null;

		var members = MemberSyntax.Parse(
			"public string T()\n{\n#if DEBUG\n\treturn @\"one\n  two\";\n#else\n\treturn @\"three\nfour\";\n#endif\n}",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\r\n",
			rewritten: literals => rewritten = literals);

		var written = members.ShouldHaveSingleItem().ToFullString();

		written.ShouldContain("@\"one\r\n  two\"", Case.Sensitive);
		written.ShouldContain("@\"three\r\nfour\"", Case.Sensitive);

		rewritten.ShouldNotBeNull().ShouldBe(
			[new MemberSyntax.RewrittenLiteral(3, 1), new MemberSyntax.RewrittenLiteral(6, 1)]);
	}

	/// <summary>
	/// A spliced body's literals are found in an inactive branch too, so a replacement whose endings
	/// landed inside one is named rather than passed over.
	/// </summary>
	[Test]
	public void Finds_a_literal_holding_a_rewritten_ending_in_an_inactive_branch()
	{
		var text = "{\r\n#if DEBUG\r\n\tvar a = @\"x\r\ny\";\r\n#endif\r\n}";

		var literals = MemberSyntax.LiteralsHolding(text, new TextSpan(0, text.Length));

		literals.ShouldHaveSingleItem().ShouldBe(new MemberSyntax.RewrittenLiteral(2, 1));
	}

	/// <summary>
	/// A literal the file already held is found again in the member it came from by its text, because
	/// what was parsed joined the body to the signature and a line counted there is one nobody can
	/// find. One the member does not hold unchanged is not found.
	/// </summary>
	[Test]
	public void Finds_a_literal_again_in_the_member_it_came_from()
	{
		var rebuilt = "void M() {\n\tvar a = @\"x\ny\";\n\tvar b = @\"p\nq\";\n}";
		var member = "void M()\n{\n\tvar a = @\"x\ny\";\n\tvar b = @\"p\nr\";\n}";

		var lines = MemberSyntax.LinesIn(
			[new MemberSyntax.RewrittenLiteral(1, 1), new MemberSyntax.RewrittenLiteral(3, 1)],
			rebuilt,
			member);

		lines.ShouldBe([2, null]);
	}

	/// <summary>
	/// A literal the caller did not write is named against the member, and the escape hatch is offered
	/// only where there was code to write a CR LF into: a body edit has it, and a move does not.
	/// </summary>
	[Test]
	public void Offers_the_escape_hatch_for_a_files_literal_only_where_code_was_sent()
	{
		MemberSyntax.RewrittenLiteral[] literals = [new(2, 1)];

		MemberSyntax.RewrittenMemberEndings(literals, "CRLF").ShouldBe(
			"Rewrote 1 line ending(s) to CRLF, the ending this file uses, inside the multi-line string literal on "
				+ "line 3 of the member, which changes its value.");

		MemberSyntax.RewrittenMemberEndings(literals, "CRLF", codeSupplied: true).ShouldEndWith(
			"which changes its value. Write one CR LF anywhere in the code sent to leave every ending in the member "
				+ "as it was.",
			Case.Sensitive);
	}

	/// <summary>
	/// A raw literal moves with the code around it. Its value is what is left once the closing
	/// delimiter's indentation comes off every line, so shifting content and delimiter together changes
	/// nothing about what the string says and everything about where it sits.
	/// </summary>
	[Test]
	public void Indents_a_raw_literal_with_the_member_around_it()
	{
		var members = MemberSyntax.Parse(
			"public const string Text = \"\"\"\nfirst\n\"\"\";\n",
			"class",
			options: null,
			indent: "\t\t");

		var written = members.ShouldHaveSingleItem().ToFullString();

		written.ShouldContain("\t\tfirst", Case.Sensitive);
		written.ShouldContain("\t\t\"\"\"", Case.Sensitive);
	}

	/// <summary>
	/// A blank line inside a raw literal comes out blank. Padding it changes the text of a string and
	/// not what the string says -- the compiler trims a whitespace-only line to nothing whatever it
	/// holds -- so nothing downstream reports it: no analyzer reads a literal's interior, and the diff
	/// shows the member rewritten around it either way.
	/// </summary>
	[Test]
	public void Leaves_a_raw_literals_blank_line_blank()
	{
		var members = MemberSyntax.Parse(
			"public const string Text = \"\"\"\nfirst\n\nsecond\n\"\"\";\n",
			"class",
			options: null,
			indent: "\t\t");

		var written = members.ShouldHaveSingleItem().ToFullString();

		written.ShouldContain("\t\tfirst\n\n\t\tsecond", Case.Sensitive);
		written.ShouldNotContain("first\n\t\t\n", Case.Sensitive);
	}

	/// <summary>
	/// The lines that did move are counted, so a caller can be told. A literal that came out where it
	/// went in reports nothing, which is what lets the two halves of the promise be told apart.
	/// </summary>
	[Test]
	public void Counts_the_literal_lines_it_moved()
	{
		var moved = -1;

		MemberSyntax.Parse(
			"public const string Text = \"\"\"\nfirst\n\nsecond\n\"\"\";\n",
			"class",
			options: null,
			indent: "\t\t",
			reindented: count => moved = count);

		// first, second, and the closing delimiter. The blank line stayed where it was.
		moved.ShouldBe(3);
	}

	/// <summary>A literal already at the destination's indentation moved nothing, and says nothing.</summary>
	[Test]
	public void Says_nothing_about_a_literal_that_did_not_move()
	{
		var moved = -1;

		// Written at the destination's own indentation, which is the shape the shift is the identity for:
		// the baseline comes off and the same amount goes back on.
		MemberSyntax.Parse(
			"\t\tpublic const string Text = \"\"\"\n\t\t\t\tfirst\n\n\t\t\t\tsecond\n\t\t\t\t\"\"\";\n",
			"class",
			options: null,
			indent: "\t\t",
			reindented: count => moved = count);

		moved.ShouldBe(-1);
	}

	/// <summary>
	/// A verbatim literal does not move, because its interior whitespace is its value and no delimiter
	/// rule takes it back out again.
	/// </summary>
	[Test]
	public void Leaves_a_verbatim_literal_where_it_is()
	{
		var members = MemberSyntax.Parse(
			"public const string Text = @\"first\nsecond\";\n",
			"class",
			options: null,
			indent: "\t\t");

		var written = members.ShouldHaveSingleItem().ToFullString();

		written.ShouldContain("\nsecond\"", Case.Sensitive);
	}
}
