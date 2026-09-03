using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Writing C# by symbol. The fixture is a repository with opinions of its own -- tabs, CRLF, a final
/// newline -- because half of what these tools promise is that code arriving in some other shape
/// ends up in the file's, and a fixture formatted like this repository could not tell the difference.
/// <para>
/// The rest of what they promise is that a bad call changes nothing and says why, and that a good
/// one says what it broke. Both are checked against the file on disk rather than against the result,
/// since a result claiming success over a mangled file is the failure being designed out.
/// </para>
/// </summary>
public sealed class MemberEditTests
{
	/// <summary>
	/// Code written the way a caller writes it -- four spaces, bare newlines -- landing in a file
	/// that wants tabs and CRLF. In a repository escalating IDE0055 the difference is a failed
	/// build, and it is the single most common thing a text edit gets wrong.
	/// </summary>
	[Fact]
	public async Task Writes_a_member_in_the_formatting_the_file_asks_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name)\n{\n    return $\"{_prefix} there, {name}!\";\n}");

		Assert.True(result.Applied);
		Assert.Equal("string Library.Greeter.Greet(string name)", result.Symbol);
		Assert.Equal(["Greet"], result.Members);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("\t\treturn $\"{_prefix} there, {name}!\";\r\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("    return", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.EndsWith("}\r\n", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A line the caller wrapped by hand, landing at the indentation of where it went rather than
	/// where it was written.
	/// <para>
	/// Found by using this tool on this repository: the formatter reindents statements and moves
	/// braces, which are rules it has, but a wrapped parameter list is layout it has no rule about,
	/// so it kept whatever arrived and the continuation lines sat a level short of their
	/// neighbours. Neither IDE0055 nor dotnet format says a word about it, which is why it needs a
	/// test of its own rather than a build to catch it.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Lines_up_a_parameter_list_the_caller_wrapped()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string, string)",
			"public string Greet(\n\tstring title,\n\tstring name)\n{\n\treturn $\"{_prefix}, {title} {name}!\";\n}");

		var text = await ReadAsync(fixture, "Greeter.cs");

		// One tab for the member, two for the parameters it wrapped onto their own lines.
		Assert.Contains(
			"\tpublic string Greet(\r\n\t\tstring title,\r\n\t\tstring name)\r\n\t{\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// The same shift must not reach inside a string. Its leading whitespace is part of the value,
	/// and in a raw literal it decides how much is stripped from every line of it.
	/// </summary>
	[Fact]
	public async Task Leaves_the_inside_of_a_multi_line_literal_alone()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Banner() => @\"\nflush left on purpose\n\";",
		});

		var text = await ReadAsync(fixture, "Greeter.cs");

		// Verbatim, endings included: a newline inside the literal is part of the value the caller
		// asked for, so normalising it to the file's CRLF would change what the program says.
		Assert.Contains("@\"\nflush left on purpose\n\";", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// The compilation happens in the same call, which is the whole reason this is not two. A body
	/// that does not compile comes back as an error against the member rather than as a build twenty
	/// seconds later.
	/// </summary>
	[Fact]
	public async Task Says_what_the_edit_broke_without_a_build()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var good = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => $\"{_prefix}, {name}.\";");

		Assert.True(good.Verified);
		Assert.Empty(good.IntroducedDiagnostics);
		Assert.Equal(0, good.TotalErrorCount);
		Assert.Contains("Library", good.ProjectsChecked);

		var bad = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => _prefix.Missing(name);");

		var introduced = Assert.Single(bad.IntroducedDiagnostics);

		Assert.Equal("CS1061", introduced.Id);
		Assert.EndsWith("Greeter.cs", introduced.FilePath, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(1, bad.TotalErrorCount);
	}

	/// <summary>
	/// The failure this tool was built for. A signature change breaks its call sites, those are in
	/// other files, and the one that reached a build undetected in the session behind all of this
	/// was found only from CS7036 -- so the answer names it, in a file the edit never touched.
	/// </summary>
	[Fact]
	public async Task Reports_the_call_site_a_changed_signature_breaks()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name, bool loud) => loud ? name.ToUpperInvariant() : name;");

		Assert.True(result.Applied);

		var introduced = Assert.Single(result.IntroducedDiagnostics);

		Assert.Equal("CS1501", introduced.Id);
		Assert.EndsWith("Caller.cs", introduced.FilePath, StringComparison.OrdinalIgnoreCase);

		// And the answer says how far it looked, since a project that only references this one was
		// not compiled and could be broken too.
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("scope=solution", StringComparison.Ordinal));
	}

	/// <summary>
	/// Code that does not parse is refused before the file is opened. Failing here costs nothing;
	/// failing at the build costs a build and leaves the file broken until someone pays for it.
	/// </summary>
	[Fact]
	public async Task Refuses_code_that_does_not_parse_and_leaves_the_file_alone()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var error = await Assert.ThrowsAsync<ArgumentException>(() => ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name)\n{\n\treturn name;\n"));

		Assert.Contains("does not parse", error.Message, StringComparison.Ordinal);
		Assert.Contains("line ", error.Message, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
	}

	/// <summary>
	/// Two overloads, and no way to tell which was meant. Writing to either would be a change that
	/// compiles, reviews as intended, and edits the wrong member -- so it refuses, names both, and
	/// takes a parameter list to settle it.
	/// </summary>
	[Fact]
	public async Task Refuses_an_overload_it_cannot_tell_apart_and_takes_a_parameter_list()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(() => ReplaceAsync(
			session,
			"Library.Greeter.Greet",
			"public string Greet(string name) => name;"));

		Assert.Contains("matches 2 declarations", error.Message, StringComparison.Ordinal);
		Assert.Contains("Greet(string name)", error.Message, StringComparison.Ordinal);
		Assert.Contains("Greet(string title, string name)", error.Message, StringComparison.Ordinal);
		Assert.Contains("parameter types", error.Message, StringComparison.Ordinal);

		var settled = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string, string)",
			"public string Greet(string title, string name) => $\"{_prefix}, {title}. {name}.\";");

		Assert.True(settled.Applied);
		Assert.Equal("string Library.Greeter.Greet(string title, string name)", settled.Symbol);
	}

	/// <summary>
	/// A caller replacing a member has usually not read the file, so it cannot have meant to delete
	/// documentation it did not know was there. Kept, and said out loud -- and replaced the moment
	/// the code carries one of its own.
	/// </summary>
	[Fact]
	public async Task Keeps_the_documentation_comment_unless_the_code_brings_one()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var kept = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => name;");

		Assert.Contains(kept.Notices, notice => notice.Contains("Kept the comment", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("\t/// <summary>The greeting for one name.</summary>\r\n\tpublic string Greet(string name) => name;", text, StringComparison.Ordinal);

		var replaced = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"/// <summary>Now documented differently.</summary>\npublic string Greet(string name) => name.Trim();");

		Assert.DoesNotContain(replaced.Notices, notice => notice.Contains("Kept the comment", StringComparison.Ordinal));

		text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("Now documented differently", text, StringComparison.Ordinal);
		Assert.DoesNotContain("The greeting for one name", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A body replacement leaves the signature untouched because it copies it rather than
	/// regenerating it, and takes whichever of the three shapes a body arrives in.
	/// </summary>
	[Fact]
	public async Task Replaces_a_body_and_leaves_the_signature_exactly_as_it_was()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// Bare statements, with no braces of their own.
		var statements = await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Greet(string)", "return name.Trim();"));

		Assert.True(statements.Applied);
		Assert.Empty(statements.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("\tpublic string Greet(string name)\r\n\t{\r\n\t\treturn name.Trim();\r\n\t}\r\n", text, StringComparison.Ordinal);
		Assert.Contains("/// <summary>The greeting for one name.</summary>", text, StringComparison.Ordinal);

		// An expression body against a member that had a block: the signature is the same either way.
		var arrow = await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Shout(string)", "=> text.ToLowerInvariant();"));

		Assert.True(arrow.Applied);

		text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("\tprivate static string Shout(string text) => text.ToLowerInvariant();\r\n", text, StringComparison.Ordinal);
	}

	/// <summary>A member with more than one body is not guessed at.</summary>
	[Fact]
	public async Task Declines_a_body_where_there_is_not_exactly_one()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(() => EditAsync(
			session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Count", "=> 3;")));

		Assert.Contains("has no body", error.Message, StringComparison.Ordinal);
		Assert.Contains("accessors", error.Message, StringComparison.Ordinal);
		Assert.Contains("rose_replace_member", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Placement is part of the request, because where a member sits is how a reader finds it, and
	/// appending everything to the end puts private helpers below the surface they serve.
	/// </summary>
	[Fact]
	public async Task Adds_members_where_it_is_told_to_put_them()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var added = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "/// <summary>How loud to be.</summary>\npublic bool Loud { get; set; }\n\npublic string Emphasise(string text) => Loud ? text.ToUpperInvariant() : text;",
			After = "PrefixLength",
		});

		Assert.True(added.Applied);
		Assert.Equal(["Loud", "Emphasise"], added.Members);
		Assert.Empty(added.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Greeter.cs");

		// A blank line either side, tab-indented, and between PrefixLength and Count.
		Assert.Contains(
			"\tpublic int PrefixLength => _prefix.Length;\r\n"
				+ "\r\n\t/// <summary>How loud to be.</summary>\r\n\tpublic bool Loud { get; set; }\r\n"
				+ "\r\n\tpublic string Emphasise(string text) => Loud ? text.ToUpperInvariant() : text;\r\n"
				+ "\r\n\tpublic int Count { get; set; }\r\n",
			text,
			StringComparison.Ordinal);

		// And at the end, when nothing says otherwise.
		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "private const int Limit = 10;",
		});

		text = await ReadAsync(fixture, "Greeter.cs");

		Assert.EndsWith("\r\n\tprivate const int Limit = 10;\r\n}\r\n", text, StringComparison.Ordinal);
	}

	/// <summary>A type with no members at all is its own case, and the one most likely to land flush against a brace.</summary>
	[Fact]
	public async Task Adds_the_first_member_of_an_empty_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Empty",
			Code = "public int Value => 1;",
		});

		var text = await ReadAsync(fixture, "Kinds.cs");

		Assert.Contains("public sealed class Empty\r\n{\r\n\tpublic int Value => 1;\r\n}\r\n", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Refuses_a_member_the_type_already_declares()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(() => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public int Count { get; set; }",
		}));

		Assert.Contains("already declares Count", error.Message, StringComparison.Ordinal);
		Assert.Contains("rose_replace_member", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Two things that cannot be placed without guessing: a partial type, where the name does not
	/// say which half, and an enum, whose members are items in a list rather than declarations.
	/// </summary>
	[Fact]
	public async Task Refuses_to_guess_which_declaration_a_member_joins()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var partial = await Assert.ThrowsAsync<ArgumentException>(() => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Split",
			Code = "public int Third => 3;",
		}));

		Assert.Contains("matches 2 declarations", partial.Message, StringComparison.Ordinal);
		Assert.Contains("Split.cs", partial.Message, StringComparison.Ordinal);
		Assert.Contains("SplitAgain.cs", partial.Message, StringComparison.Ordinal);
		Assert.Contains("filePath", partial.Message, StringComparison.Ordinal);

		var settled = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Split",
			Code = "public int Third => 3;",
			FilePath = fixture.Path("Members", "Library", "SplitAgain.cs"),
		});

		Assert.True(settled.Applied);
		Assert.Contains("Third", await ReadAsync(fixture, "SplitAgain.cs"));
		Assert.DoesNotContain("Third", await ReadAsync(fixture, "Split.cs"));

		var @enum = await Assert.ThrowsAsync<ArgumentException>(() => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Colour",
			Code = "Blue = 2",
		}));

		Assert.Contains("is an enum", @enum.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A member edit rewrites the member and not the file. A repository whose endings are already
	/// inconsistent would otherwise get every line rewritten by a one-member change, which buries
	/// the edit in a diff nobody can review.
	/// </summary>
	[Fact]
	public async Task Leaves_the_lines_it_did_not_write_exactly_as_they_were()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		// The whole file given the wrong endings behind the workspace's back, as a stray tool would.
		await File.WriteAllTextAsync(
			path,
			original.Replace("\r\n", "\n", StringComparison.Ordinal),
			TestContext.Current.CancellationToken);

		await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Greet(string)", "return name.Trim();"));

		var text = await ReadAsync(fixture, "Greeter.cs");

		// The member that was written obeys .editorconfig ...
		Assert.Contains("\t/// <summary>The greeting for one name.</summary>\r\n", text, StringComparison.Ordinal);
		Assert.Contains("\t\treturn name.Trim();\r\n", text, StringComparison.Ordinal);

		// ... and every member that was not is still exactly as it was found, bare newlines and all.
		Assert.Contains("private readonly string _prefix = \"Hello\";\n", text, StringComparison.Ordinal);
		Assert.Contains("public int PrefixLength => _prefix.Length;\n", text, StringComparison.Ordinal);
		Assert.Contains("return text.ToUpperInvariant();\n", text, StringComparison.Ordinal);
		Assert.EndsWith("}\n", text, StringComparison.Ordinal);

		// Most of the file is untouched: only the written member and the lines it adjoins were
		// rewritten, which is what keeps a one-member change reviewable.
		var normalised = text.Split("\r\n").Length - 1;

		Assert.InRange(normalised, 6, 10);
	}

	[Fact]
	public async Task Writes_nothing_when_previewing_and_still_says_what_it_would_break()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Library.Greeter.Greet(string)",
			Code = "public string Greet(string name, bool loud) => name;",
			Apply = false,
		});

		Assert.False(result.Applied);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
		Assert.Contains("Preview only", string.Join(" ", result.Notices), StringComparison.Ordinal);

		// The diff and the breakage are the point of asking: both describe a change that did not happen.
		Assert.Contains("bool loud", result.Diff, StringComparison.Ordinal);
		Assert.Contains(result.IntroducedDiagnostics, diagnostic => diagnostic.Id == "CS1501");
	}

	/// <summary>
	/// An unverified edit has to say so. An empty introduced list means nothing at all when nothing
	/// was compiled, and reads exactly like a clean result.
	/// </summary>
	[Fact]
	public async Task Says_when_it_did_not_compile_anything()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Library.Greeter.Greet(string)",
			Code = "public string Greet(string name) => name.Missing();",
			Verify = false,
		});

		Assert.True(result.Applied);
		Assert.False(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Contains("Nothing was compiled", string.Join(" ", result.Notices), StringComparison.Ordinal);
	}

	/// <summary>
	/// Naming something that is not there is answered with what is, so a mistyped name is fixed from
	/// the message rather than by going back and reading the file.
	/// </summary>
	[Fact]
	public async Task Says_what_it_found_when_the_name_is_wrong()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var missing = await Assert.ThrowsAsync<ArgumentException>(() => ReplaceAsync(
			session, "Library.Greeter.Salute", "public string Salute() => _prefix;"));

		Assert.Contains("Nothing in the solution is called 'Salute'", missing.Message, StringComparison.Ordinal);
		Assert.Contains("rose_search_symbols", missing.Message, StringComparison.Ordinal);

		var elsewhere = await Assert.ThrowsAsync<ArgumentException>(() => ReplaceAsync(
			session, "Library.Caller.Shout(string)", "private static string Shout(string text) => text;"));

		Assert.Contains("Nothing is declared at 'Library.Caller.Shout(string)'", elsewhere.Message, StringComparison.Ordinal);
		Assert.Contains("Library.Greeter.Shout", elsewhere.Message, StringComparison.Ordinal);
	}

	private static Task<MemberEditResult> ReplaceAsync(WorkspaceSession session, string symbol, string code) =>
		EditAsync(session, Request(MemberEditKind.Replace, symbol, code));

	private static MemberEditRequest Request(MemberEditKind kind, string symbol, string code) =>
		new() { Kind = kind, Symbol = symbol, Code = code };

	private static Task<MemberEditResult> EditAsync(WorkspaceSession session, MemberEditRequest request)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		return session.MutateAsync(
			(snapshot, token) => MemberEditService.EditAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);
	}

	private static Task<string> ReadAsync(FixtureSolution fixture, string file) =>
		File.ReadAllTextAsync(fixture.Path("Members", "Library", file), TestContext.Current.CancellationToken);
}
