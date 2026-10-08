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

				// The word, its closing quote and the semicolon after it: half inside the literal, half code.
				Find = "total\";",
				Replace = "count\";",
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

	/// <summary>
	/// Code inserted at either end of a body lands among statements it did not write, and the blank lines
	/// between those statements are theirs. Rebuilt by joining the statements one to a line, a body loses
	/// every blank line but the ones beside the insertion: a change nobody asked for, which the overreach
	/// sentence names and nothing should have made.
	/// </summary>
	[Test]
	[Arguments(BodyPosition.Start)]
	[Arguments(BodyPosition.End)]
	public async Task Keeps_the_blank_lines_between_the_statements_it_inserts_beside(BodyPosition position)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", "Spaced.cs"),
			"""
			namespace Library;

			public static class Spaced
			{
				public static int Sum(int[] values)
				{
					var total = 0;

					foreach (var value in values)
					{
						total += value;
					}

					return total;
				}
			}

			""".ReplaceLineEndings("\r\n"),
			TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Spaced.Sum",
			Position = position,
			Code = "ArgumentNullException.ThrowIfNull(values);",
		});

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Spaced.cs");

		// The blank line under the first statement and the one under the loop, whichever end was written.
		text.ShouldContain("\t\tvar total = 0;\r\n\r\n\t\tforeach", Case.Sensitive);
		text.ShouldContain("\t\t}\r\n\r\n\t\t", Case.Sensitive);

		result.Notices.ShouldNotContain(
			notice => notice.Contains("Nothing this was asked to do reaches them", StringComparison.Ordinal));
	}

	/// <summary>
	/// A whole body is parsed behind the signature it was copied from and inside braces it never had,
	/// so a line counted in what is parsed is one the caller never wrote. The literal is named on its
	/// line in the body they sent, blank line above it included.
	/// </summary>
	[Test]
	public async Task Names_a_rewritten_literal_on_its_line_in_the_body_supplied()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Code = "\nvar greeting = text.Trim();\nvar banner = @\"one\ntwo\";\nreturn banner + greeting;",
		});

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldContain(
			notice => notice.StartsWith("Rewrote 1 line ending(s) to CRLF", StringComparison.Ordinal)
				&& notice.Contains("literal on line 3 of the code supplied", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"one\r\ntwo\"", Case.Sensitive);
	}

	/// <summary>
	/// Inserted code lands among statements it did not write, and the literal it carries is still named
	/// on its line in what was sent -- while the statements around it, all on one line each, have their
	/// endings rewritten without a word.
	/// </summary>
	[Test]
	public async Task Names_a_rewritten_literal_on_its_line_in_the_code_inserted()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Position = BodyPosition.End,
			Code = "text = text.Trim();\ntext += @\"one\ntwo\";",
		});

		result.Applied.ShouldBeTrue();

		var rewrote = result.Notices.Where(notice => notice.StartsWith("Rewrote", StringComparison.Ordinal)).ToArray();

		rewrote.ShouldHaveSingleItem().ShouldContain("literal on line 2 of the code supplied", Case.Sensitive);
	}

	/// <summary>
	/// A replacement for part of an expression body on its signature's line has no indentation to take
	/// off and none to put on, and its blank line above still goes, as it does wherever a replacement
	/// lands. So the literal it carries is named on the line the caller wrote it on.
	/// </summary>
	[Test]
	public async Task Names_a_literal_replacing_part_of_a_one_line_expression_body_on_its_line()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Arrowed.Call",
			Find = "Describe(\"one\", \"two\")",
			Replace = "\n@\"a\nb\"",
		});

		result.Applied.ShouldBeTrue();

		var rewrote = result.Notices.Where(notice => notice.StartsWith("Rewrote", StringComparison.Ordinal)).ToArray();

		rewrote.ShouldHaveSingleItem().ShouldContain("literal on line 2 of the code supplied", Case.Sensitive);

		var text = await ReadAsync(fixture, "Arrowed.cs");

		text.ShouldContain("public static string Call() => @\"a\r\nb\";", Case.Sensitive);
	}

	/// <summary>
	/// A token-matched replacement inside a block body is counted from its first line with anything on
	/// it, which is the line it is spliced at, so a literal two lines further down is named there.
	/// </summary>
	[Test]
	public async Task Names_a_literal_a_token_matched_replacement_carries_on_its_line()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Find = "text.ToUpperInvariant()",
			Replace = "\ntext.ToUpperInvariant()\n\t+ @\"one\ntwo\"",
		});

		result.Applied.ShouldBeTrue();

		var rewrote = result.Notices.Where(notice => notice.StartsWith("Rewrote", StringComparison.Ordinal)).ToArray();

		rewrote.ShouldHaveSingleItem().ShouldContain("literal on line 3 of the code supplied", Case.Sensitive);

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"one\r\ntwo\"", Case.Sensitive);
	}

	/// <summary>
	/// A literal the file already held has its endings rewritten when the whole body it sits in is bare
	/// LFs, and it is named on its line in the member as it stood -- the third, below the signature and
	/// the brace -- rather than in what was parsed, where the brace joins the signature. The way to keep
	/// its endings is offered, because the body edit took code to write a CR LF into.
	/// </summary>
	[Test]
	public async Task Names_a_files_own_literal_on_its_line_in_the_member()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await WithLineFeedLiteralAsync(fixture);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Find = "text.ToUpperInvariant()",
			Replace = "text.ToLowerInvariant()",
		});

		result.Applied.ShouldBeTrue();

		// The file's own sentence about the lines it normalised is a different notice, about layout.
		var rewrote = result.Notices.Where(notice => notice.Contains("inside the multi-line", StringComparison.Ordinal)).ToArray();

		rewrote.ShouldHaveSingleItem().ShouldBe(
			"Rewrote 1 line ending(s) to CRLF, the ending this file uses, inside the multi-line string literal on "
				+ "line 3 of the member, which changes its value. Write one CR LF anywhere in the code sent to leave "
				+ "every ending in the member as it was.");

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"one\r\ntwo\"", Case.Sensitive);
	}

	/// <summary>
	/// The escape hatch that sentence offers does what it says: one CR LF in the replacement leaves the
	/// file's own literal with the bare LF it had, and nothing claims a literal's endings were rewritten.
	/// </summary>
	[Test]
	public async Task Keeps_a_files_own_literal_as_it_was_when_the_replacement_carries_a_carriage_return()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await WithLineFeedLiteralAsync(fixture);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Find = "return banner + text.ToUpperInvariant();",
			Replace = "var shouted = text.ToLowerInvariant();\r\n\t\treturn banner + shouted;",
		});

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldNotContain(notice => notice.Contains("inside the multi-line", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"one\ntwo\"", Case.Sensitive);
		text.ShouldContain("var shouted = text.ToLowerInvariant();", Case.Sensitive);
	}

	/// <summary>
	/// A literal in a branch the project's symbols leave inactive is still compiled by the build that
	/// defines the symbol, so the whitespace pass leaves its interior alone -- the bare LF and the trailing
	/// spaces both -- and a body written with a CR LF claims no ending was rewritten. Its LF does fail
	/// <c>dotnet format</c> like any other, and is reported as such.
	/// </summary>
	[Test]
	public async Task Leaves_a_literal_in_an_inactive_branch_exactly_as_it_was_written()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Code = "#if ROSE_NEVER_DEFINED\r\n\t\tvar s = @\"a  \nb\";\r\n#endif\r\n\t\treturn text.ToUpperInvariant();",
		});

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldNotContain(notice => notice.Contains("inside the multi-line", StringComparison.Ordinal));
		result.Notices.ShouldContain(notice => notice.Contains("now fails dotnet format", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"a  \nb\"", Case.Sensitive);
	}

	/// <summary>
	/// A one-line insertion into a CRLF block leaves a literal of the file's own, holding a deliberate
	/// bare LF, exactly as it was: the statements around the insertion keep the block's ending, so
	/// nothing reads the block as one written by somebody with no view about endings.
	/// </summary>
	[Test]
	public async Task Leaves_a_files_literal_alone_when_one_line_is_inserted_beside_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await ReplaceShoutBodyAsync(
			fixture,
			"\t{\r\n\t\tvar x = @\"a\nb\";\r\n\t\treturn x + text.ToUpperInvariant();\r\n\t}");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Position = BodyPosition.End,
			Code = "text = text.Trim();",
		});

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldNotContain(notice => notice.Contains("inside the multi-line", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"a\nb\"", Case.Sensitive);
		text.ShouldContain("text = text.Trim();", Case.Sensitive);
	}

	/// <summary>
	/// Removing the first of two identical literals the file holds leaves the second one named on its own
	/// line in the member, not on the line of the one that went.
	/// </summary>
	[Test]
	public async Task Names_the_remaining_copy_of_a_literal_on_its_own_line()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await ReplaceShoutBodyAsync(
			fixture,
			"\t{\n\t\tvar a = @\"x\ny\";\n\t\tvar b = @\"x\ny\";\n\t\treturn b + text.ToUpperInvariant();\n\t}");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Find = "var a = @\"x\ny\";",
			Replace = string.Empty,
		});

		result.Applied.ShouldBeTrue();

		var rewrote = result.Notices.Where(notice => notice.Contains("inside the multi-line", StringComparison.Ordinal)).ToArray();

		rewrote.ShouldHaveSingleItem().ShouldContain("literal on line 5 of the member", Case.Sensitive);
	}

	/// <summary>
	/// A replacement takes the ending the repository declares, not the one the file was checked out
	/// with: where .editorconfig asks for LF in a CRLF checkout, the caller's LF literal is left as it was,
	/// agreeing with the lines the write gives the member, and nothing claims an ending was rewritten.
	/// </summary>
	[Test]
	public async Task Gives_a_replacement_the_ending_the_repository_declares()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", ".editorconfig"),
			"[*.cs]\nend_of_line = lf\n",
			TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Greeter.Shout",
			Find = "text.ToUpperInvariant()",
			Replace = "@\"one\ntwo\" + text",
		});

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldNotContain(notice => notice.Contains("inside the multi-line", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"one\ntwo\"", Case.Sensitive);
		text.ShouldNotContain("@\"one\r\ntwo\"", Case.Sensitive);
	}

	/// <summary>Gives Shout the body supplied, behind the workspace's back, before a session opens.</summary>
	private static async Task ReplaceShoutBodyAsync(FixtureSolution fixture, string replacement)
	{
		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var cancellation = TestContext.Current!.Execution.CancellationToken;
		var original = await File.ReadAllTextAsync(path, cancellation);

		var body = "\t{\r\n\t\treturn text.ToUpperInvariant();\r\n\t}";

		original.ShouldContain(body, Case.Sensitive);

		await File.WriteAllTextAsync(path, original.Replace(body, replacement, StringComparison.Ordinal), cancellation);
	}

	/// <summary>
	/// Gives Shout a body written entirely with bare LFs, a verbatim literal included, in a file that is
	/// otherwise CRLF: the shape in which the endings of a literal the caller never wrote are rewritten.
	/// </summary>
	private static Task WithLineFeedLiteralAsync(FixtureSolution fixture) =>
		ReplaceShoutBodyAsync(
			fixture,
			"\t{\n\t\tvar banner = @\"one\ntwo\";\n\t\treturn banner + text.ToUpperInvariant();\n\t}");

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
