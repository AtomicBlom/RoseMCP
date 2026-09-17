using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.MemberEdits;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Writing C# by symbol: adding, replacing and removing a member, and refusing a call it cannot
/// resolve to exactly one. The refusals are half of it, and they are checked against the file rather
/// than the result -- a bad call has to change nothing and say why, and "nothing" is a property of
/// the file on disk.
/// <para>
/// Enums are here rather than apart because what they exercise is the same promise wearing a harder
/// hat: a value added to an enum has to keep the commas and layout the file uses, and has to report
/// when it renumbers everything after it. The imports are here for the same reason -- a written
/// member that turned out to need a namespace gets one, and where two namespaces would do, it reports
/// rather than chooses.
/// </para>
/// </summary>
public sealed class MemberEditTests
{
	/// <summary>
	/// Code that does not parse is refused before the file is opened. Failing here costs nothing;
	/// failing at the build costs a build and leaves the file broken until someone pays for it.
	/// </summary>
	[Test]
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
	[Test]
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
	[Test]
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
	[Test]
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
	[Test]
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
	[Test]
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
	[Test]
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

	[Test]
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
	[Test]
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
	}

	/// <summary>
	/// An enum's values are items in a comma-separated list, so adding one is a splice that has to put
	/// the comma on the right side of the line break and follow the enum's own layout -- here, a blank
	/// line between values and a trailing comma after the last.
	/// </summary>
	[Test]
	public async Task Adds_values_to_an_enum_with_its_commas_and_layout_kept()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var appended = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Colour",
			Code = "Blue = 5",
		});

		Assert.True(appended.Applied);
		Assert.Equal(["Blue"], appended.Members);
		Assert.Empty(appended.IntroducedDiagnostics);

		// In front of a value with an initialiser of its own, so nothing after it is renumbered.
		var between = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Colour",
			After = "Green",
			Code = "Yellow = 7,",
		});

		Assert.True(between.Applied);
		Assert.Empty(between.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Kinds.cs");

		Assert.Contains(
			"{\r\n\tRed,\r\n\r\n\tGreen,\r\n\r\n\tYellow = 7,\r\n\r\n\tBlue = 5,\r\n}\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// The shape where a text edit gets the comma wrong: no trailing comma, so adding at the end has to
	/// give the old last value one, and documentation on every value with no blank lines between.
	/// </summary>
	[Test]
	public async Task Adds_documented_values_to_an_enum_without_a_trailing_comma()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var appended = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Access",
			Code = "/// <summary>May run.</summary>\nExecute = 4",
		});

		Assert.True(appended.Applied);
		Assert.Empty(appended.IntroducedDiagnostics);

		var inserted = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Access",
			Before = "Read",
			Code = "/// <summary>May read and write.</summary>\nReadWrite = Read | Write,",
		});

		Assert.True(inserted.Applied);
		Assert.Empty(inserted.IntroducedDiagnostics);

		var text = await ReadAsync(fixture, "Access.cs");

		Assert.Contains(
			"{\r\n"
				+ "\t/// <summary>Nothing at all.</summary>\r\n\tNone = 0,\r\n"
				+ "\t/// <summary>May read and write.</summary>\r\n\tReadWrite = Read | Write,\r\n"
				+ "\t/// <summary>May read.</summary>\r\n\tRead = 1,\r\n"
				+ "\t/// <summary>May write.</summary>\r\n\tWrite = 2,\r\n"
				+ "\t/// <summary>May run.</summary>\r\n\tExecute = 4\r\n"
				+ "}\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// A name already taken never compiles, so it is refused with the file untouched. A number already
	/// taken and an implicit value that renumbers the ones after it both compile and are sometimes
	/// meant -- a [Flags] enum gives one value two names routinely -- so they are written and said, and
	/// an alias written by naming the value it equals is not even mentioned.
	/// </summary>
	[Test]
	public async Task Reports_an_enum_value_that_collides_or_renumbers_the_ones_after_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var original = await ReadAsync(fixture, "Kinds.cs");

		Task<MemberEditResult> Add(string code, string? after = null) => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Colour",
			After = after,
			Code = code,
		});

		var name = await Assert.ThrowsAsync<ArgumentException>(() => Add("Green = 3"));
		Assert.Contains("already declares Green", name.Message, StringComparison.Ordinal);

		var anchor = await Assert.ThrowsAsync<ArgumentException>(() => Add("Blue = 9", after: "Purple"));
		Assert.Contains("no value called 'Purple'", anchor.Message, StringComparison.Ordinal);
		Assert.Contains("Red, Green", anchor.Message, StringComparison.Ordinal);

		Assert.Equal(original, await ReadAsync(fixture, "Kinds.cs"));

		var alias = await Add("Crimson = Red");

		Assert.True(alias.Applied);
		Assert.DoesNotContain(alias.Notices, notice => notice.Contains("cannot be told apart", StringComparison.Ordinal));

		var value = await Add("Blue = 1");

		Assert.True(value.Applied);
		Assert.Contains(value.Notices, notice => notice.Contains("Blue is 1, which Green already is", StringComparison.Ordinal));

		var renumbered = await Add("Purple", after: "Red");

		Assert.True(renumbered.Applied);
		Assert.Contains(renumbered.Notices, notice => notice.Contains("renumbers Green from 1 to 2", StringComparison.Ordinal));

		Assert.Contains(
			"{\r\n\tRed,\r\n\r\n\tPurple,\r\n\r\n\tGreen,\r\n\r\n\tCrimson = Red,\r\n\r\n\tBlue = 1,\r\n}",
			await ReadAsync(fixture, "Kinds.cs"),
			StringComparison.Ordinal);
	}

	/// <summary>
	/// An enum over a byte, with values written in hex, as a shift and from a constant. The values are
	/// compared in the enum's own type, and one the type cannot hold is written and then reported by the
	/// compile rather than refused, since it is an error the compiler already names.
	/// </summary>
	[Test]
	public async Task Adds_values_to_an_enum_over_a_byte()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		Task<MemberEditResult> Add(string code, string? after = null) => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Priority",
			After = after,
			Code = code,
		});

		var medium = await Add("Medium = 0x08", after: "Low");

		Assert.True(medium.Applied);
		Assert.Empty(medium.IntroducedDiagnostics);
		Assert.DoesNotContain(medium.Notices, notice => notice.Contains("renumbers", StringComparison.Ordinal));

		Assert.Contains(
			"{\r\n\tLow = 0x01,\r\n\tMedium = 0x08,\r\n\tHigh = 1 << 4,\r\n\tHighest = byte.MaxValue,\r\n}",
			await ReadAsync(fixture, "Priority.cs"),
			StringComparison.Ordinal);

		var urgent = await Add("Urgent = 0x10");

		Assert.True(urgent.Applied);
		Assert.Contains(urgent.Notices, notice => notice.Contains("Urgent is 16, which High already is", StringComparison.Ordinal));

		var over = await Add("Over = 0x100");

		Assert.True(over.Applied);
		Assert.Contains(over.IntroducedDiagnostics, diagnostic => diagnostic.Id == "CS0031");
	}

	/// <summary>
	/// Naming something that is not there is answered with what is, so a mistyped name is fixed from
	/// the message rather than by going back and reading the file.
	/// </summary>
	[Test]
	public async Task Says_what_it_found_when_the_name_is_wrong()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// Its own type, and the only refusal here that has one: a read may answer this one from metadata
		// instead, where nothing may answer a name that is in source somewhere other than where asked.
		var missing = await Assert.ThrowsAsync<SymbolNotFoundException>(() => ReplaceAsync(
			session, "Library.Greeter.Salute", "public string Salute() => _prefix;"));

		Assert.Contains("Nothing in the solution is called 'Salute'", missing.Message, StringComparison.Ordinal);
		Assert.Contains("rose_search_symbols", missing.Message, StringComparison.Ordinal);

		var elsewhere = await Assert.ThrowsAsync<ArgumentException>(() => ReplaceAsync(
			session, "Library.Caller.Shout(string)", "private static string Shout(string text) => text;"));

		Assert.Contains("Nothing is declared at 'Library.Caller.Shout(string)'", elsewhere.Message, StringComparison.Ordinal);
		Assert.Contains("Library.Greeter.Shout", elsewhere.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Dropping an attribute the caller never saw leaves valid C# that compiles and verifies clean
	/// while the member has quietly left whatever the attribute enrolled it in -- an [McpServerTool]
	/// off the surface, a [Test] out of the run. There is no symptom until something is missing
	/// somewhere else, so the old attributes are kept and named.
	/// </summary>
	[Test]
	public async Task Keeps_the_attributes_a_replacement_does_not_declare()
	{
		using var fixture = await AttributedGreeterAsync();
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ReplaceAsync(
			session,
			"Library.Greeter.Shout(string)",
			"private static string Shout(string text)\n{\n\treturn text.ToUpperInvariant() + \"!\";\n}");

		Assert.True(result.Applied);

		var after = await ReadAsync(fixture, "Greeter.cs");

		// Comment above attribute above declaration, each on its own line at the file's indentation:
		// the comment is content the caller did not supply, and the attribute is now the first token.
		Assert.Contains(
			"\t/// <summary>Louder.</summary>\r\n\t[Obsolete(\"Shout is going away.\")]\r\n\tprivate static string Shout(string text)",
			after,
			StringComparison.Ordinal);

		Assert.Contains("ToUpperInvariant() + \"!\"", after, StringComparison.Ordinal);

		// Named rather than counted, so a caller can tell whether the one it cares about survived.
		Assert.Contains(result.Notices, notice => notice.Contains("[Obsolete]", StringComparison.Ordinal));
	}

	/// <summary>
	/// The other half of the rule, and the way a caller removes one: attributes in the code are the
	/// attributes written, so replacing them or leaving them off is a decision the caller can make.
	/// </summary>
	[Test]
	public async Task Takes_the_attributes_a_replacement_declares()
	{
		using var fixture = await AttributedGreeterAsync();
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ReplaceAsync(
			session,
			"Library.Greeter.Shout(string)",
			"[Obsolete(\"Use Announce instead.\")]\nprivate static string Shout(string text) => text.ToUpperInvariant();");

		Assert.True(result.Applied);

		var after = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("[Obsolete(\"Use Announce instead.\")]", after, StringComparison.Ordinal);
		Assert.DoesNotContain("Shout is going away.", after, StringComparison.Ordinal);
		Assert.DoesNotContain(result.Notices, notice => notice.Contains("Kept [Obsolete]", StringComparison.Ordinal));
	}

	/// <summary>
	/// A copy of the Members fixture whose Shout carries a documentation comment and an attribute.
	/// Written into the copy rather than into the checked-in fixture, which every test counting its
	/// members would see. Both, because the two share one piece of trivia: a declaration's
	/// documentation comment sits above the first of its attributes, so carrying attributes over moves
	/// which token the comment is attached to.
	/// </summary>
	private static async Task<FixtureSolution> AttributedGreeterAsync()
	{
		var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		var greeter = fixture.Path("Members", "Library", "Greeter.cs");
		var text = await File.ReadAllTextAsync(greeter, TestContext.Current!.Execution.CancellationToken);

		await File.WriteAllTextAsync(
			greeter,
			text.Replace(
				"\tprivate static string Shout(string text)",
				"\t/// <summary>Louder.</summary>\r\n"
					+ "\t[Obsolete(\"Shout is going away.\")]\r\n"
					+ "\tprivate static string Shout(string text)",
				StringComparison.Ordinal),
			TestContext.Current!.Execution.CancellationToken);

		return fixture;
	}

	/// <summary>
	/// A member edit resolves its own imports too, off the compilation that was already built to say
	/// what the edit broke. Reporting the namespace and stopping is a round trip at the moment the
	/// caller was promised there would not be one.
	/// </summary>
	[Test]
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
	[Test]
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
	[Test]
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
	/// An ambiguous name is refused rather than resolved. Removing one of two overloads is the deletion
	/// with no symptom: it compiles, and the behaviour that was meant to change did not.
	/// </summary>
	[Test]
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
	[Test]
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
	[Test]
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
	/// An import fetched for a name and the error it was fetched for surviving are two facts the
	/// result used to carry side by side without joining them: "imported Library.Extras, the only
	/// namespace anything of that name is in" beside an error about the same name, and nothing saying
	/// the first had not fixed the second.
	/// <para>
	/// The join is decidable rather than a guess -- an import for a name that still does not bind is
	/// the wrong import -- and it is what turns the outcome a sole candidate is allowed to have into
	/// one sentence rather than two facts the caller has to put together. Here the only thing called
	/// <c>Shouted</c> is a method, the code uses the name as a type, and the namespace is right about
	/// where the name lives and wrong about what it is.
	/// </para>
	/// </summary>
	[Test]
	public async Task Says_when_an_import_did_not_resolve_the_error_it_was_fetched_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public Shouted? Loud() => null;",
		});

		Assert.Contains(result.IntroducedDiagnostics, entry => entry.Id == "CS0246");

		Assert.Contains(
			result.Notices,
			notice => notice.Contains("Shouted", StringComparison.Ordinal)
				&& notice.Contains("Library.Extras", StringComparison.Ordinal)
				&& notice.Contains("did not resolve", StringComparison.Ordinal));
	}
}
