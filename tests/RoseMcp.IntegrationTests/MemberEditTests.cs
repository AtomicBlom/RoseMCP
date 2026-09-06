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
	/// The same shift must not reach inside a verbatim string. Its leading whitespace is part of the
	/// value, and no delimiter rule takes it back out again, so a literal written flush left stays
	/// flush left however deep the member around it sits.
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

		// The line the literal holds is not indented with the member. Its endings are the file's,
		// because the code arrived carrying none of its own.
		Assert.Contains("@\"\r\nflush left on purpose\r\n\";", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A multi-line literal composed for a JSON argument arrives with bare newlines, which in a CRLF
	/// file fails dotnet format while no build complains and the obvious fix changes what the program
	/// says. The endings become the file's, and the result says so, because a diff cannot show a
	/// terminator and this one is part of a string's value.
	/// <para>
	/// A caller that writes a carriage return is thinking about endings, and then nothing is touched --
	/// which is also the way to ask for a bare newline inside a literal on purpose.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Rewrites_the_endings_of_a_literal_composed_without_them()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var bare = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Bare() => @\"\nline one\nline two\n\";",
		});

		Assert.Contains(
			bare.Notices,
			notice => notice.Contains("Rewrote", StringComparison.Ordinal)
				&& notice.Contains("line ending(s) in the code supplied", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("@\"\r\nline one\r\nline two\r\n\";", text, StringComparison.Ordinal);

		// Supplied with the file's own endings, there is nothing to rewrite and nothing to say.
		var matching = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Matching() => @\"\r\nline one\r\nline two\r\n\";",
		});

		Assert.DoesNotContain(
			matching.Notices,
			notice => notice.Contains("line ending(s) in the code supplied", StringComparison.Ordinal));
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

	/// <summary>
	/// The signature comes out exactly as it went in, wrapping included.
	/// <para>
	/// Where a hand-wrapped parameter list sits is layout Roslyn's formatter has no rule about, and
	/// neither IDE0055 nor dotnet format has an opinion either -- so a tool that re-indents one does
	/// it silently, and the diff of a one-line body change grows a signature nobody touched. Replacing
	/// a body promises the signature cannot drift; this is the cheapest place that promise can break.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Leaves_a_wrapped_signature_exactly_as_it_was()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(
			session,
			Request(MemberEditKind.ReplaceBody, "Library.Wrapped.Join", "return string.Concat(first, second, third);"));

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Wrapped.cs");

		Assert.Contains(
			"\tpublic static string Join(\r\n\t\tstring first,\r\n\t\tstring second,\r\n\t\tstring third)\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// A hand-wrapped call inside a replaced body keeps its continuation level, whatever indentation
	/// the caller wrote it at.
	/// <para>
	/// The mirror image of the signature trap above, and the more expensive one, because the body is
	/// what this tool exists to change. A continuation line is not a statement, so Roslyn's formatter
	/// has no rule that puts one back, and neither IDE0055 nor dotnet format has an opinion about a
	/// wrapped argument list -- so the code comes out a level short of its neighbours and every build
	/// passes. The three spellings are one request: the caller's own baseline cannot decide where the
	/// code lands.
	/// </para>
	/// </summary>
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public async Task Keeps_a_wrapped_call_in_a_body_a_level_in(int written)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var baseline = new string('\t', written);

		var code = $"{baseline}return string.Concat(\n{baseline}\tfirst,\n{baseline}\tsecond,\n{baseline}\tthird);";

		var result = await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Wrapped.Join", code));

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Wrapped.cs");

		Assert.Contains(
			"\t{\r\n\t\treturn string.Concat(\r\n\t\t\tfirst,\r\n\t\t\tsecond,\r\n\t\t\tthird);\r\n\t}",
			text,
			StringComparison.Ordinal);
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

	/// <summary>
	/// A public member reshaped breaks its dependents by construction, so the projects that reference
	/// this one are compiled too. Checking only the file's own would report a clean edit at exactly the
	/// moment it is not one -- which is the confident-answer-to-a-different-question this whole surface
	/// is built to avoid.
	/// </summary>
	[Fact]
	public async Task Compiles_the_dependents_of_a_public_member()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Multiply",
			Code = "public static int Multiply(int left, int right, int scale) => left * right * scale;",
		});

		Assert.Contains("App", result.ProjectsChecked);
		Assert.Contains(
			result.IntroducedDiagnostics,
			entry => entry.FilePath!.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
		Assert.Empty(result.DependentsNotChecked);
	}

	/// <summary>
	/// Narrowing the scope by hand is allowed and is not silent: the same edit reports nothing wrong,
	/// and says which dependents nobody looked at. Reporting no introduced errors without that is a
	/// clean bill of health for half the question.
	/// </summary>
	[Fact]
	public async Task Names_the_dependents_a_narrowed_scope_skipped()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Multiply",
			Code = "public static int Multiply(int left, int right, int scale) => left * right * scale;",
			VerifyScope = VerifyScope.File,
		});

		Assert.DoesNotContain("App", result.ProjectsChecked);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Contains("App", result.DependentsNotChecked);
	}

	/// <summary>
	/// A private member cannot be seen outside the projects holding it however the edit reshapes it,
	/// so the wide scope is not paid for. Effective accessibility, not declared: a public member of a
	/// private nested type is private too.
	/// </summary>
	[Fact]
	public async Task Leaves_a_private_member_in_its_own_projects()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Twice",
			Code = "private static int Twice(int value, int times) => value * times;",
		});

		Assert.Equal(["Core"], result.ProjectsChecked);
		Assert.Empty(result.DependentsNotChecked);
	}

	/// <summary>
	/// A body cannot be seen outside at all: the signature that comes out is the one that was there,
	/// copied rather than rewritten, so nothing downstream can be looking at anything different.
	/// </summary>
	[Fact]
	public async Task Leaves_a_body_change_in_its_own_projects()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Core.Calculator.Multiply",
			Code = "=> right * left;",
		});

		Assert.Equal(["Core"], result.ProjectsChecked);
	}

	/// <summary>
	/// A member edit resolves its own imports too, off the compilation that was already built to say
	/// what the edit broke. Reporting the namespace and stopping is a round trip at the moment the
	/// caller was promised there would not be one.
	/// </summary>
	[Fact]
	public async Task Imports_what_a_written_member_turned_out_to_need()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public byte[] Bytes() => Encoding.UTF8.GetBytes(_prefix);",
		});

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Contains(result.Notices, notice => notice.Contains("imported System.Text", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("using System.Text;", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// Two candidates is a choice the caller has to make. Nothing is imported, and the reason is said
	/// rather than left as a bare unresolved name, which would send them off to write a type that
	/// already exists twice.
	/// </summary>
	[Fact]
	public async Task Reports_rather_than_chooses_between_two_namespaces()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Colour() => Palette.Name;",
		});

		Assert.Contains(
			result.Notices,
			notice => notice.Contains("Palette is in 2 namespaces", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.DoesNotContain("using Library.Left;", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A private helper with nothing referencing it, which is the case that made this a gap: an unused
	/// private is IDE0051, a build error in this repository, and removing it meant finding a line range
	/// and cutting text in a session whose whole point was not doing that.
	/// </summary>
	[Fact]
	public async Task Removes_a_member_with_its_documentation_comment()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Delete,
			Symbol = "Library.Regioned.Thrice",
		});

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Equal(["Thrice"], result.Members);

		var text = await ReadAsync(fixture, "Regioned.cs");

		Assert.DoesNotContain("Thrice", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Trebles it", text, StringComparison.Ordinal);
		Assert.Contains("Twice", text, StringComparison.Ordinal);

		// The region survives, balanced. Cutting a line range takes one half of a pair and leaves the
		// file with CS1024 or CS1028, which is the class of failure this exists to remove.
		Assert.Contains("#region Helpers", text, StringComparison.Ordinal);
		Assert.Contains("#endregion", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\r\n\r\n\r\n", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// Removing something still referenced is allowed -- the callers may be going too -- and the call
	/// sites come back as the errors it introduced rather than at the next build.
	/// </summary>
	[Fact]
	public async Task Reports_what_a_removal_broke_across_the_dependents()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Delete,
			Symbol = "Core.Calculator.Multiply",
		});

		Assert.True(result.Applied);
		Assert.Contains("App", result.ProjectsChecked);
		Assert.Contains(
			result.IntroducedDiagnostics,
			entry => entry.FilePath!.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
		Assert.Empty(result.DependentsNotChecked);
	}

	/// <summary>
	/// An ambiguous name is refused rather than resolved. Removing one of two overloads is the deletion
	/// with no symptom: it compiles, and the behaviour that was meant to change did not.
	/// </summary>
	[Fact]
	public async Task Refuses_to_remove_an_ambiguous_name()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.Delete,
				Symbol = "Library.Greeter.Greet",
			}));

		Assert.Contains("matches 2 declarations", thrown.Message, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
	}

	/// <summary>The named overload goes and the other stays.</summary>
	[Fact]
	public async Task Removes_the_overload_the_parameter_list_names()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Delete,
			Symbol = "Library.Greeter.Greet(string, string)",
		});

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.DoesNotContain("string title", text, StringComparison.Ordinal);
		Assert.Contains("public string Greet(string name)", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// Removing the only member of a type leaves a type, not a syntax error. The braces collapse onto
	/// something that now needs different formatting, which is one of the things a text edit gets wrong.
	/// </summary>
	[Fact]
	public async Task Removes_the_last_member_of_a_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Delete,
			Symbol = "Library.IShape.Area",
		});

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Kinds.cs");

		Assert.Contains("public interface IShape", text, StringComparison.Ordinal);
		Assert.DoesNotContain("double Area()", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A one-token change without re-emitting the body. Anchoring inside a member already resolved by
	/// name keeps the ambiguity surface to one body, and what reaches disk is still a whole body,
	/// parsed and formatted.
	/// </summary>
	[Fact]
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

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("dear {name}", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// The anchor is matched on the tokens, so indentation and line endings cannot cause a miss -- a
	/// real way a text edit fails in a repository whose files disagree about either.
	/// </summary>
	[Fact]
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

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("ToLowerInvariant", text, StringComparison.Ordinal);
	}

	/// <summary>An anchor that matches nothing changes nothing and says what to do about it.</summary>
	[Fact]
	public async Task Refuses_an_anchor_that_matches_nothing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Greeter.Shout",
				Find = "return text.Trim();",
				Replace = "return text;",
			}));

		Assert.Contains("does not contain", thrown.Message, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
	}

	/// <summary>
	/// Inserting at the end means before a closing return, because anything after one is unreachable
	/// and CS0162 -- and the result says so rather than leaving the caller to notice.
	/// </summary>
	[Fact]
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

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("before the closing return", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");
		var trimmed = text.IndexOf("text = text.Trim();", StringComparison.Ordinal);
		var returned = text.IndexOf("return text.ToUpperInvariant();", StringComparison.Ordinal);

		Assert.True(trimmed > 0 && trimmed < returned);
	}

	/// <summary>An expression body has no statement list, and the refusal says what to pass instead.</summary>
	[Fact]
	public async Task Refuses_to_insert_into_an_expression_body()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Greeter.Greet(string, string)",
				Position = BodyPosition.Start,
				Code = "var x = 1;",
			}));

		Assert.Contains("has an expression body", thrown.Message, StringComparison.Ordinal);
		Assert.Contains("Pass code with the whole body", thrown.Message, StringComparison.Ordinal);
	}

	/// <summary>Two ways of saying what the body becomes is ambiguous, and refused rather than ranked.</summary>
	[Fact]
	public async Task Refuses_two_payloads_at_once()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Greeter.Shout",
				Code = "return text;",
				Find = "text.ToUpperInvariant()",
				Replace = "text",
			}));

		Assert.Contains("Pass one of code", thrown.Message, StringComparison.Ordinal);
	}
}
