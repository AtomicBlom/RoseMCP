using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// The parse that happens before anything is written. Everything here is a refusal that costs
/// nothing, standing in for a build that costs twenty seconds and a file left broken until someone
/// pays for it -- and every case below is drawn from a failure that actually reached disk when the
/// same edit went through a text tool.
/// </summary>
public sealed class MemberSyntaxTests
{
	[Fact]
	public void Parses_the_members_the_code_declares()
	{
		var members = Parse("public int Count { get; set; }\n\npublic void Reset() => Count = 0;");

		Assert.Equal(2, members.Count);
		Assert.IsType<PropertyDeclarationSyntax>(members[0]);
		Assert.IsType<MethodDeclarationSyntax>(members[1]);
	}

	/// <summary>
	/// The failure with no error at all. Code that closes the container early and opens something of
	/// its own leaves a file that parses perfectly, with a type nobody asked for at the top level --
	/// so the shape has to be checked rather than only the syntax.
	/// </summary>
	[Fact]
	public void Refuses_code_that_would_land_outside_the_member()
	{
		var error = Assert.Throws<ArgumentException>(() => Parse("public void M() { } } public class Escaped {"));

		Assert.Contains("closes more braces", error.Message, StringComparison.Ordinal);
	}

	/// <summary>An unbalanced brace on its own is a parse error, and reported as one.</summary>
	[Fact]
	public void Refuses_a_stray_closing_brace()
	{
		var error = Assert.Throws<ArgumentException>(() => Parse("public void M()\n{\n}\n}"));

		Assert.Contains("does not parse", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Refuses_a_brace_that_is_never_closed_and_says_where()
	{
		var error = Assert.Throws<ArgumentException>(() => Parse("public void M()\n{\n\tif (true)\n\t{\n"));

		Assert.Contains("does not parse", error.Message, StringComparison.Ordinal);
		Assert.Contains("line ", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The escapes that leaked into source and cost nine compile errors were this: a shell ate one
	/// layer of quoting and what landed was not C#. It is caught here rather than at the build.
	/// </summary>
	[Fact]
	public void Refuses_source_with_escapes_left_in_it()
	{
		var error = Assert.Throws<ArgumentException>(() => Parse("public string M() => \\$\"{Value}\";"));

		Assert.Contains("does not parse", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A using directive inside a member position is a scope mistake rather than a typing one, so
	/// the message says what the tool writes instead of leaving a parser error to be decoded.
	/// </summary>
	[Fact]
	public void Says_that_a_using_directive_is_not_a_member()
	{
		var error = Assert.Throws<ArgumentException>(() => Parse("using System.Text;\n\npublic void M() { }"));

		Assert.Contains("using directive", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A comment after the last member attaches to the closing brace of its container, so it belongs
	/// to no member and would vanish. Silently losing a comment is invisible in a diff nobody reads.
	/// </summary>
	[Fact]
	public void Refuses_a_comment_that_would_be_dropped()
	{
		var error = Assert.Throws<ArgumentException>(() => Parse("public void M() { }\n\n// and another thing"));

		Assert.Contains("belongs to no member", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Refuses_code_that_declares_nothing()
	{
		Assert.Throws<ArgumentException>(() => Parse("// just a comment"));
		Assert.Throws<ArgumentException>(() => Parse("   "));
	}

	/// <summary>
	/// The documentation comment has to arrive attached to the member, or replacing a declaration
	/// would drop the documentation the caller wrote for it.
	/// </summary>
	[Fact]
	public void Keeps_a_documentation_comment_with_the_member_it_describes()
	{
		var members = Parse("/// <summary>Counts.</summary>\npublic int Count { get; set; }");

		Assert.Single(members);
		Assert.Contains(members[0].GetLeadingTrivia(), MemberSyntax.IsComment);
	}

	/// <summary>
	/// Parsed in a container of the right kind, because a member is only meaningful in one. An enum
	/// member is not a declaration anywhere else, and a bodiless method is only ordinary inside an
	/// interface.
	/// </summary>
	[Theory]
	[InlineData("enum", "Blue = 3")]
	[InlineData("interface", "double Area();")]
	[InlineData("struct", "public readonly int X;")]
	[InlineData("record", "public int Y { get; init; }")]
	public void Parses_a_member_in_the_container_it_belongs_to(string keyword, string code)
	{
		Assert.Single(MemberSyntax.Parse(code, keyword, null));
	}

	/// <summary>
	/// An enum member parses only against an enum, which is the case the container keyword exists
	/// for: a class wrapper turns the same text into a field with no type and rejects it.
	/// </summary>
	[Fact]
	public void Refuses_an_enum_member_offered_to_a_class()
	{
		Assert.Throws<ArgumentException>(() => Parse("Blue = 3"));
	}

	[Theory]
	[InlineData("class C { }", "class")]
	[InlineData("interface I { }", "interface")]
	[InlineData("enum E { }", "enum")]
	[InlineData("record R { }", "record")]
	[InlineData("record struct S { }", "record struct")]
	public void Reports_the_keyword_a_container_was_declared_with(string declaration, string expected)
	{
		var tree = CSharpSyntaxTree.ParseText(declaration, cancellationToken: TestContext.Current.CancellationToken);
		var unit = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
		var type = (BaseTypeDeclarationSyntax)unit.Members[0];

		Assert.Equal(expected, MemberSyntax.KeywordOf(type));
	}

	/// <summary>
	/// The half of formatting the formatter does not do. It reindents statements and moves braces,
	/// so a line wrapped inside a body comes out right, but a wrapped parameter list is layout it
	/// has no rule about and keeps whatever arrived -- and neither IDE0055 nor dotnet format has an
	/// opinion either, so code written for column zero lands a level short of its neighbours and
	/// nothing complains.
	/// </summary>
	[Fact]
	public void Shifts_wrapped_lines_to_the_indentation_of_where_they_are_going()
	{
		var members = MemberSyntax.Parse(
			"public void Write(\n\tint count,\n\tstring name)\n{\n\tSend(count);\n}",
			"class",
			null,
			"\t");

		var text = Assert.Single(members).ToFullString();

		// A member at one tab wraps its parameters at two and holds its body at two.
		Assert.Contains("\n\t\tint count,", text, StringComparison.Ordinal);
		Assert.Contains("\n\t\tstring name)", text, StringComparison.Ordinal);
		Assert.Contains("\n\t{", text, StringComparison.Ordinal);
		Assert.Contains("\n\t\tSend(count);", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A caller that has read the file and indented for the destination is as likely as one that
	/// wrote at column zero, and the two have to be the same request -- which is why the baseline
	/// comes off before the destination's indentation goes on.
	/// </summary>
	[Fact]
	public void Treats_code_already_indented_for_its_destination_the_same_way()
	{
		var atColumnZero = MemberSyntax.Parse(
			"public void Write(\n\tint count)\n{\n\tSend(count);\n}", "class", null, "\t");

		var preIndented = MemberSyntax.Parse(
			"\tpublic void Write(\n\t\tint count)\n\t{\n\t\tSend(count);\n\t}", "class", null, "\t");

		Assert.Equal(
			Assert.Single(atColumnZero).ToFullString(),
			Assert.Single(preIndented).ToFullString());
	}

	/// <summary>
	/// A declaration arriving as its own source text, indentation and all, which is what a move
	/// hands over. Its first line carries the level it was written at, so that is the baseline, and
	/// every wrapped line keeps the relation it had to it.
	/// </summary>
	[Fact]
	public void Measures_a_moved_declaration_against_the_indentation_it_arrives_with()
	{
		var members = MemberSyntax.Parse(
			"\tpublic static string Join(\n\t\tstring first,\n\t\tstring second)\n\t{\n\t\treturn first;\n\t}",
			"class",
			null,
			"\t");

		var text = Assert.Single(members).ToFullString();

		Assert.Contains("\n\t\tstring first,", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\n\t\t\tstring first,", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// The line inside a string is content, not layout. Shifting it changes what the program says,
	/// and in a raw literal it changes how much is stripped from every other line of the value.
	/// </summary>
	[Fact]
	public void Leaves_the_lines_inside_a_multi_line_literal_where_they_are()
	{
		var members = MemberSyntax.Parse(
			"public string Text() => @\"\nkeep me here\n\";", "class", null, "\t");

		var text = Assert.Single(members).ToFullString();

		Assert.Contains("\nkeep me here\n", text, StringComparison.Ordinal);
	}

	private static IReadOnlyList<MemberDeclarationSyntax> Parse(string code) =>
		MemberSyntax.Parse(code, "class", null);

	/// <summary>
	/// A lone LF becomes the destination's ending, inside a raw literal as well as outside one. The
	/// code arrives as a JSON argument, which carries no carriage return, so keeping what arrived keeps
	/// an artefact of the transport -- and produces a file dotnet format rejects while no build says
	/// anything.
	/// </summary>
	[Fact]
	public void Rewrites_a_lone_line_feed_to_the_destination_ending()
	{
		var rewritten = 0;

		var members = MemberSyntax.Parse(
			"public const string Text = \"\"\"\nfirst\nsecond\n\"\"\";\n",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\r\n",
			count => rewritten = count);

		var written = Assert.Single(members).ToFullString();

		Assert.Contains("\"\"\"\r\n\tfirst\r\n\tsecond\r\n\t\"\"\"", written, StringComparison.Ordinal);
		Assert.Equal(4, rewritten);
	}

	/// <summary>
	/// A carriage return the caller wrote deliberately survives, so a file that is otherwise LF can
	/// still be given a CRLF literal.
	/// </summary>
	[Fact]
	public void Keeps_a_carriage_return_the_caller_supplied()
	{
		var rewritten = 0;

		MemberSyntax.Parse(
			"public const string Text = \"\"\"\r\nfirst\r\n\"\"\";\r\n",
			"class",
			options: null,
			indent: "\t",
			lineEnding: "\n",
			count => rewritten = count);

		Assert.Equal(0, rewritten);
	}

	/// <summary>
	/// A raw literal moves with the code around it. Its value is what is left once the closing
	/// delimiter's indentation comes off every line, so shifting content and delimiter together changes
	/// nothing about what the string says and everything about where it sits.
	/// </summary>
	[Fact]
	public void Indents_a_raw_literal_with_the_member_around_it()
	{
		var members = MemberSyntax.Parse(
			"public const string Text = \"\"\"\nfirst\n\"\"\";\n",
			"class",
			options: null,
			indent: "\t\t");

		var written = Assert.Single(members).ToFullString();

		Assert.Contains("\t\tfirst", written, StringComparison.Ordinal);
		Assert.Contains("\t\t\"\"\"", written, StringComparison.Ordinal);
	}

	/// <summary>
	/// A verbatim literal does not move, because its interior whitespace is its value and no delimiter
	/// rule takes it back out again.
	/// </summary>
	[Fact]
	public void Leaves_a_verbatim_literal_where_it_is()
	{
		var members = MemberSyntax.Parse(
			"public const string Text = @\"first\nsecond\";\n",
			"class",
			options: null,
			indent: "\t\t");

		var written = Assert.Single(members).ToFullString();

		Assert.Contains("\nsecond\"", written, StringComparison.Ordinal);
	}
}
