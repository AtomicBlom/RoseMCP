using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

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

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("\t/// <summary>The greeting for one name, shouted.</summary>\r\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("The greeting for one name.", text, StringComparison.Ordinal);

		// The code under it is untouched, which is the whole point of the tool being separate.
		Assert.Contains("public string Greet(string name)", text, StringComparison.Ordinal);
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

		Assert.Contains("/// <summary>Says hello.</summary>\r\n", text, StringComparison.Ordinal);
		Assert.Contains("/// <remarks>\r\n", text, StringComparison.Ordinal);
		Assert.Contains("/// At various lengths.\r\n", text, StringComparison.Ordinal);
		Assert.Contains("/// </remarks>\r\n", text, StringComparison.Ordinal);
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

		Assert.True(result.Applied);
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("had no documentation comment", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("/// <summary>How many greetings have gone out.</summary>", text, StringComparison.Ordinal);
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

		Assert.Contains(
			"public int Count { get; set; }\r\n"
				+ "\r\n"
				+ "\t/// <summary>The greeting for one name, shouted.</summary>\r\n"
				+ "\tpublic string Greet(string name)",
			text,
			StringComparison.Ordinal);
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

		Assert.Contains(
			"public int PrefixLength => _prefix.Length;\r\n"
				+ "\r\n"
				+ "\t/// <summary>How many greetings have gone out.</summary>\r\n"
				+ "\tpublic int Count { get; set; }",
			text,
			StringComparison.Ordinal);
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

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => CommentAsync(session, "Library.Greeter.Greet(string)", "<summary>Unclosed"));

		Assert.Contains("not well-formed XML", thrown.Message, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
	}

	[Test]
	public async Task Adds_an_attribute_that_was_not_there()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AttributeAsync(
			session, "Library.Greeter.Greet(string)", "Obsolete(\"use Greet(title, name)\")", AttributeAction.Set);

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("[Obsolete(\"use Greet(title, name)\")]", text, StringComparison.Ordinal);

		// The documentation comment stays above the attribute, which is where a declaration puts it.
		var comment = text.IndexOf("<summary>The greeting for one name", StringComparison.Ordinal);
		var attribute = text.IndexOf("[Obsolete", StringComparison.Ordinal);

		Assert.True(comment < attribute);
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

		Assert.True(result.Applied, "the attribute is written; only its layout is under test");

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains(
			"\t[Obsolete(\r\n\t\t\"use Greet\",\r\n\t\terror: false)]\r\n\tpublic string Greet(string name)",
			text,
			StringComparison.Ordinal);
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

		Assert.True(result.Applied, "the attribute is written; the body is what is under test");

		var text = await ReadAsync(fixture, "Arrowed.cs");

		Assert.Contains("\t[Obsolete(\"use Describe\")]\r\n\tpublic static string Spread(", text, StringComparison.Ordinal);

		// Two tabs for the body, three for the lines it wraps onto, exactly as before.
		Assert.Contains(
			"\t\tfirst\r\n\t\t\t+ \", \" + second\r\n\t\t\t+ \", \" + third;",
			text,
			StringComparison.Ordinal);
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

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("Greet([Marked] string name)", text, StringComparison.Ordinal);
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

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => AttributeAsync(
				session, "Library.Greeter.Greet(string)", "Marked", AttributeAction.Add, parameter: "missing"));

		Assert.Contains("no parameter called missing", error.Message, StringComparison.Ordinal);
		Assert.Contains("name", error.Message, StringComparison.Ordinal);
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

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => AttributeAsync(session, "Library.Greeter.Count", "Marked(3)", AttributeAction.Set));

		Assert.Contains("carries 2 attributes called Marked", thrown.Message, StringComparison.Ordinal);
		Assert.Contains("Marked(1)", thrown.Message, StringComparison.Ordinal);
		Assert.Contains("Marked(2)", thrown.Message, StringComparison.Ordinal);
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

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.DoesNotContain("Marked", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[]", text, StringComparison.Ordinal);
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

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("Marked(2)", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Marked(1)", text, StringComparison.Ordinal);
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

		await Assert.ThrowsAsync<ArgumentException>(
			() => AttributeAsync(session, "Library.Greeter.Count", "Obsolete(\"unclosed", AttributeAction.Set));

		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
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
}
