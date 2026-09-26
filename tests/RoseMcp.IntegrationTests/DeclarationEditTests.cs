using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Editing what surrounds a declaration rather than the code inside it. This is the half of an edit
/// that had no tool, and the half two recorded sessions blamed for using none of the others: a
/// repository whose members carry fifteen lines of documentation is one where composing the whole
/// member to change a sentence loses to an editor every time.
/// </summary>
public sealed class DeclarationEditTests
{
	[Test]
	public async Task Replaces_a_summary_written_as_plain_text()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await CommentAsync(session, "Library.Greeter.Greet(string)", "The greeting for one name, shouted.");

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("\t/// <summary>The greeting for one name, shouted.</summary>\r\n", Case.Sensitive);
		text.ShouldNotContain("The greeting for one name.", Case.Sensitive);

		// The code under it is untouched, which is the whole point of the tool being separate.
		text.ShouldContain("public string Greet(string name)", Case.Sensitive);
	}

	/// <summary>
	/// XML is passed through as written, one line per line, with the file's own indentation and
	/// prefix. A repository that writes long comments needs the multi-line form to be the easy one.
	/// </summary>
	[Test]
	public async Task Writes_multi_line_xml_in_the_files_own_style()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await CommentAsync(
			session,
			"Library.Greeter",
			"<summary>Says hello.</summary>\n<remarks>\nAt various lengths.\n</remarks>");

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("/// <summary>Says hello.</summary>\r\n", Case.Sensitive);
		text.ShouldContain("/// <remarks>\r\n", Case.Sensitive);
		text.ShouldContain("/// At various lengths.\r\n", Case.Sensitive);
		text.ShouldContain("/// </remarks>\r\n", Case.Sensitive);
	}

	/// <summary>
	/// A member with no comment gets one, and is told so rather than left to infer it from a diff.
	/// </summary>
	[Test]
	public async Task Adds_a_comment_where_there_was_none()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await CommentAsync(session, "Library.Greeter.Count", "How many greetings have gone out.");

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldContain(
			notice => notice.Contains("had no documentation comment", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("/// <summary>How many greetings have gone out.</summary>", Case.Sensitive);
	}

	/// <summary>
	/// The blank line separating a member from the one above it stays above the comment. Writing the
	/// new comment first and everything that survived after it moves that line underneath, which
	/// leaves every replaced comment joined to the member above and separated from the declaration it
	/// documents -- a change to the whole neighbourhood from a call that promised to touch a sentence,
	/// and one nothing reports.
	/// </summary>
	[Test]
	public async Task Leaves_the_blank_line_above_a_comment_where_it_was()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await CommentAsync(session, "Library.Greeter.Greet(string)", "The greeting for one name, shouted.");

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain(
			"public int Count { get; set; }\r\n"
				+ "\r\n"
				+ "\t/// <summary>The greeting for one name, shouted.</summary>\r\n"
				+ "\tpublic string Greet(string name)", Case.Sensitive);
	}

	/// <summary>
	/// The same where there was no comment to replace: it goes immediately above the declaration,
	/// under the blank line rather than over it.
	/// </summary>
	[Test]
	public async Task Puts_a_new_comment_under_the_blank_line_above_the_member()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await CommentAsync(session, "Library.Greeter.Count", "How many greetings have gone out.");

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain(
			"public int PrefixLength => _prefix.Length;\r\n"
				+ "\r\n"
				+ "\t/// <summary>How many greetings have gone out.</summary>\r\n"
				+ "\tpublic int Count { get; set; }", Case.Sensitive);
	}

	/// <summary>
	/// XML that opens a tag it never closes is CS1570, a build error where the analyzers are turned
	/// up. Refused before the file is opened, so nothing is written.
	/// </summary>
	[Test]
	public async Task Refuses_xml_that_does_not_parse()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => CommentAsync(session, "Library.Greeter.Greet(string)", "<summary>Unclosed")).OfExactType();

		thrown.Message.ShouldContain("not well-formed XML", Case.Sensitive);
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
	}

	[Test]
	public async Task Adds_an_attribute_that_was_not_there()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AttributeAsync(
			session, "Library.Greeter.Greet(string)", "Obsolete(\"use Greet(title, name)\")", AttributeAction.Set);

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("[Obsolete(\"use Greet(title, name)\")]", Case.Sensitive);

		// The documentation comment stays above the attribute, which is where a declaration puts it.
		var comment = text.IndexOf("<summary>The greeting for one name", StringComparison.Ordinal);
		var attribute = text.IndexOf("[Obsolete", StringComparison.Ordinal);

		(comment < attribute).ShouldBeTrue();
	}

	/// <summary>
	/// An attribute whose argument list the caller wrapped keeps that shape and lands at the
	/// declaration's own level, whether they wrote it at column zero, already indented for where it
	/// goes, or opening with a line break. Roslyn's formatter has no rule about where a wrapped list
	/// sits, so an attribute composed at column zero stays at column zero under a declaration several
	/// levels in -- and neither IDE0055 nor <c>dotnet format</c> has an opinion about it either, so
	/// nothing says so.
	/// <para>
	/// The leading-break case is the first-line half of the same question. Unlike a bare parameter
	/// list, a fragment spliced into a line has a first line of its own, and it belongs at the splice
	/// point however many blank lines precede it -- which is why the line exempted from the shift is
	/// the first one with content on it rather than the first one there is.
	/// </para>
	/// </summary>
	[Test]
	[Arguments("Obsolete(\n\t\"use Greet\",\n\terror: false)")]
	[Arguments("\t\tObsolete(\n\t\t\t\"use Greet\",\n\t\t\terror: false)")]
	[Arguments("\nObsolete(\n\t\"use Greet\",\n\terror: false)")]
	[Arguments("\n\t\tObsolete(\n\t\t\t\"use Greet\",\n\t\t\terror: false)")]
	public async Task Keeps_the_shape_of_a_wrapped_attribute(string attribute)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AttributeAsync(session, "Library.Greeter.Greet(string)", attribute, AttributeAction.Add);

		result.Applied.ShouldBeTrue("the attribute is written; only its layout is under test");

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain(
			"\t[Obsolete(\r\n\t\t\"use Greet\",\r\n\t\terror: false)]\r\n\tpublic string Greet(string name)", Case.Sensitive);
	}

	/// <summary>
	/// An attribute onto a member whose expression body is wrapped. The attribute goes above the
	/// declaration and the body is none of its business, but both go through the same indentation
	/// pass -- so the body is what says whether that pass reached further than it was asked to.
	/// </summary>
	[Test]
	public async Task Leaves_a_wrapped_expression_body_alone_when_it_writes_an_attribute()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AttributeAsync(
			session, "Library.Arrowed.Spread", "Obsolete(\"use Describe\")", AttributeAction.Add);

		result.Applied.ShouldBeTrue("the attribute is written; the body is what is under test");

		var text = await ReadAsync(fixture, "Arrowed.cs");

		text.ShouldContain("\t[Obsolete(\"use Describe\")]\r\n\tpublic static string Spread(", Case.Sensitive);

		// Two tabs for the body, three for the lines it wraps onto, exactly as before.
		text.ShouldContain(
			"\t\tfirst\r\n\t\t\t+ \", \" + second\r\n\t\t\t+ \", \" + third;", Case.Sensitive);
	}

	/// <summary>
	/// An attribute on a parameter, which a declaration name cannot reach. It goes in front of the
	/// type on the same line, because that is where a parameter's attribute sits and a line break
	/// there is layout nothing downstream has a rule about.
	/// </summary>
	[Test]
	public async Task Writes_an_attribute_onto_a_parameter()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AttributeAsync(
			session, "Library.Greeter.Greet(string)", "Marked", AttributeAction.Add, parameter: "name");

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("Greet([Marked] string name)", Case.Sensitive);
	}

	/// <summary>
	/// A parameter name that matches nothing is refused with the names that are there, rather than
	/// writing the attribute onto the declaration and reporting success -- which is the shape of
	/// failure that looks exactly like the change working.
	/// </summary>
	[Test]
	public async Task Refuses_a_parameter_the_member_does_not_have()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => AttributeAsync(
				session, "Library.Greeter.Greet(string)", "Marked", AttributeAction.Add, parameter: "missing")).OfExactType();

		error.Message.ShouldContain("no parameter called missing", Case.Sensitive);
		error.Message.ShouldContain("name", Case.Sensitive);
	}

	/// <summary>
	/// Replacing one of several attributes of a name would compile and change the wrong case, which
	/// is the failure with no symptom. It is refused, and the refusal lists what it found.
	/// </summary>
	[Test]
	public async Task Refuses_to_set_where_several_attributes_share_a_name()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await AttributeAsync(session, "Library.Greeter.Count", "Marked(1)", AttributeAction.Add);
		await AttributeAsync(session, "Library.Greeter.Count", "Marked(2)", AttributeAction.Add);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => AttributeAsync(session, "Library.Greeter.Count", "Marked(3)", AttributeAction.Set)).OfExactType();

		thrown.Message.ShouldContain("carries 2 attributes called Marked", Case.Sensitive);
		thrown.Message.ShouldContain("Marked(1)", Case.Sensitive);
		thrown.Message.ShouldContain("Marked(2)", Case.Sensitive);
	}

	/// <summary>
	/// Removing the only attribute in a bracket takes the bracket with it. An empty [] does not
	/// compile, and leaving one would be a syntax error written by a tool that parses everything.
	/// </summary>
	[Test]
	public async Task Removes_an_attribute_and_its_brackets()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await AttributeAsync(session, "Library.Greeter.Count", "Marked(1)", AttributeAction.Add);

		var result = await AttributeAsync(session, "Library.Greeter.Count", "Marked", AttributeAction.Remove);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldNotContain("Marked", Case.Sensitive);
		text.ShouldNotContain("[]", Case.Sensitive);
	}

	/// <summary>
	/// Obsolete and ObsoleteAttribute are the same attribute, and a caller should not have to know
	/// which spelling the file used.
	/// </summary>
	[Test]
	public async Task Matches_an_attribute_whether_or_not_it_is_spelled_with_the_suffix()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await AttributeAsync(session, "Library.Greeter.Count", "MarkedAttribute(1)", AttributeAction.Add);

		var result = await AttributeAsync(session, "Library.Greeter.Count", "Marked(2)", AttributeAction.Set);

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("Marked(2)", Case.Sensitive);
		text.ShouldNotContain("Marked(1)", Case.Sensitive);
	}

	/// <summary>
	/// An attribute that does not parse is refused before the file is opened, the same promise every
	/// other write here makes.
	/// </summary>
	[Test]
	public async Task Refuses_an_attribute_that_does_not_parse()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		await Should.ThrowAsync<ArgumentException>(
			() => AttributeAsync(session, "Library.Greeter.Count", "Obsolete(\"unclosed", AttributeAction.Set)).OfExactType();

		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
	}

	private static Task<MemberEditResult> CommentAsync(WorkspaceSession session, string symbol, string comment)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);
		var request = new DeclarationEditRequest { Symbol = symbol, Comment = comment };

		return session.MutateAsync(
			(snapshot, token) => DeclarationEditService.ReplaceDocCommentAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	private static Task<MemberEditResult> AttributeAsync(
		WorkspaceSession session,
		string symbol,
		string attribute,
		AttributeAction action,
		string? parameter = null)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new DeclarationEditRequest
		{
			Symbol = symbol,
			Attribute = attribute,
			Action = action,
			Parameter = parameter,

			// The fixture has no attribute class of its own, so an unresolved one would report an
			// error that says nothing about whether the edit landed where it was aimed.
			Verify = false,
		};

		return session.MutateAsync(
			(snapshot, token) => DeclarationEditService.SetAttributeAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	private static Task<string> ReadAsync(FixtureSolution fixture, string file) =>
		File.ReadAllTextAsync(fixture.Path("Members", "Library", file), TestContext.Current!.Execution.CancellationToken);

	/// <summary>
	/// An edit that introduces nothing does not say the project compiles clean when it does not. The
	/// two questions a result answers are "what did this edit break" and "what is broken", and
	/// answering the first in the words of the second tells a caller their project is sound at the
	/// moment it is not.
	/// <para>
	/// Asserted here rather than only for member edits because this tool reached that answer by a
	/// different rule: it read "introduced nothing" as "compiles clean", so a project with errors
	/// already in it was reported clean by a comment change.
	/// </para>
	/// </summary>
	[Test]
	public async Task Does_not_call_a_project_clean_when_errors_were_already_in_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// One error to be pre-existing by the time the comment is written, in the same project the
		// comment lives in, since a documentation comment is verified against its own project.
		var broken = await MemberEdits.EditAsync(
			session,
			MemberEdits.Request(MemberEditKind.Add, "Library.Prose", "public static string Missing() => Absent.Name;"));

		broken.IntroducedDiagnostics.ShouldNotBeEmpty();

		var result = await CommentAsync(session, "Library.Greeter.Greet(string)", "The greeting, with the project already broken elsewhere.");

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var said = string.Join(" ", result.Notices);

		said.ShouldNotContain("compiles clean", Case.Sensitive);
		said.ShouldContain("were there before this edit", Case.Sensitive);
	}
}
