using RoseMcp.Contracts;
using RoseMcp.TestSupport;

using static RoseMcp.IntegrationTests.MemberEdits;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Changing part of a body rather than re-emitting the member: find-and-replace, anchored edits and
/// insertion. These address text inside code, which is the one place a member edit has no symbol to
/// aim at, so what they hold to is that a match is found where it means something and refused where
/// it does not -- a match straddling code and a string literal changes nothing, and an anchor that
/// matches nothing is an error rather than a no-op reported as success.
/// </summary>
public sealed class MemberEditBodyTests
{
	/// <summary>
	/// A sentence inside a <c>//</c> comment, which the token matching cannot see at all. Without
	/// this the only way to change one is to re-emit the whole member, which is what sends a caller
	/// back to a text editor and takes the rest of this surface with them.
	/// </summary>
	[Test]
	public async Task Changes_a_line_comment_inside_a_body()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Prose.Counted",
			Find = "which is not the same as what was asked for",
			Replace = "which is the only thing this can honestly report",
			IncludeTrivia = true,
		});

		var text = await ReadAsync(fixture, "Prose.cs");

		text.ShouldContain("// Counts what is there, which is the only thing this can honestly report.", Case.Sensitive);
	}

	/// <summary>
	/// The body of a string constant. Every tool description in this repository is one, and changing a
	/// sentence in one meant re-emitting a fifty-line declaration to alter a clause.
	/// </summary>
	[Test]
	public async Task Changes_a_sentence_inside_a_string_constant()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Prose.Description",
			Find = "Pass a path to say which thing.",
			Replace = "Name it, rather than pointing at a line.",
			IncludeTrivia = true,
		});

		var text = await ReadAsync(fixture, "Prose.cs");

		text.ShouldContain(
			"public const string Description = \"Reads a thing. Name it, rather than pointing at a line.\";", Case.Sensitive);
	}

	/// <summary>
	/// The whole initialiser, written as code rather than matched as text. A constant has no body in
	/// the sense a method does, and refusing one on that basis is what left this text with no tool.
	/// </summary>
	[Test]
	public async Task Writes_a_whole_constant_initialiser()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Prose.Description",
			Code = "\"Replaced outright.\"",
		});

		var text = await ReadAsync(fixture, "Prose.cs");

		text.ShouldContain("public const string Description = \"Replaced outright.\";", Case.Sensitive);
	}

	/// <summary>
	/// A match covering part of a string and part of the code around it rewrites a delimiter rather
	/// than the text inside one, so it is refused. Left to run, what comes out either does not parse
	/// or parses as something else with the rest of the body swallowed into a literal.
	/// </summary>
	[Test]
	public async Task Refuses_a_match_that_straddles_code_and_text()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Prose.Label",

				// The closing quote and the semicolon after it: half inside the literal, half code.
				Find = "\"total\";",
				Replace = "\"count\";",
				IncludeTrivia = true,
			})).OfExactType();

		error.Message.ShouldContain("part of a string and part of the code", Case.Sensitive);
	}

	/// <summary>
	/// A one-token change without re-emitting the body. Anchoring inside a member already resolved by
	/// name keeps the ambiguity surface to one body, and what reaches disk is still a whole body,
	/// parsed and formatted.
	/// </summary>
	[Test]
	public async Task Changes_part_of_a_body_by_anchor()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Greet(string)",
			Find = "$\"{_prefix}, {name}!\"",
			Replace = "$\"{_prefix}, dear {name}!\"",
		});

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("dear {name}", Case.Sensitive);
	}

	/// <summary>
	/// The anchor is matched on the tokens, so indentation and line endings cannot cause a miss -- a
	/// real way a text edit fails in a repository whose files disagree about either.
	/// </summary>
	[Test]
	public async Task Matches_an_anchor_whose_spacing_is_not_the_files()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Find = "return    text . ToUpperInvariant ( ) ;",
			Replace = "return text.ToLowerInvariant();",
		});

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("ToLowerInvariant", Case.Sensitive);
	}

	/// <summary>An anchor that matches nothing changes nothing and says what to do about it.</summary>
	[Test]
	public async Task Refuses_an_anchor_that_matches_nothing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Greeter.Shout",
				Find = "return text.Trim();",
				Replace = "return text;",
			})).OfExactType();

		thrown.Message.ShouldContain("does not contain", Case.Sensitive);
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
	}

	/// <summary>
	/// Inserting at the end means before a closing return, because anything after one is unreachable
	/// and CS0162 -- and the result says so rather than leaving the caller to notice.
	/// </summary>
	[Test]
	public async Task Inserts_before_a_closing_return()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Position = BodyPosition.End,
			Code = "text = text.Trim();",
		});

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldContain(
			notice => notice.Contains("before the closing return", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");
		var trimmed = text.IndexOf("text = text.Trim();", StringComparison.Ordinal);
		var returned = text.IndexOf("return text.ToUpperInvariant();", StringComparison.Ordinal);

		(trimmed > 0 && trimmed < returned).ShouldBeTrue();
	}

	/// <summary>An expression body has no statement list, and the refusal says what to pass instead.</summary>
	[Test]
	public async Task Refuses_to_insert_into_an_expression_body()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Greeter.Greet(string, string)",
				Position = BodyPosition.Start,
				Code = "var x = 1;",
			})).OfExactType();

		thrown.Message.ShouldContain("has an expression body", Case.Sensitive);
		thrown.Message.ShouldContain("Pass code with the whole body", Case.Sensitive);
	}

	/// <summary>Two ways of saying what the body becomes is ambiguous, and refused rather than ranked.</summary>
	[Test]
	public async Task Refuses_two_payloads_at_once()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Greeter.Shout",
				Code = "return text;",
				Find = "text.ToUpperInvariant()",
				Replace = "text",
			})).OfExactType();

		thrown.Message.ShouldContain("Pass one of code", Case.Sensitive);
	}
}
