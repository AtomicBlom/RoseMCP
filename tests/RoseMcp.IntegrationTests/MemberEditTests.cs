using RoseMcp.Contracts;
using RoseMcp.TestSupport;

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

		var error = await Should.ThrowAsync<ArgumentException>(() => ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name)\n{\n\treturn name;\n")).OfExactType();

		error.Message.ShouldContain("does not parse", Case.Sensitive);
		error.Message.ShouldContain("line ", Case.Sensitive);
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
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

		var error = await Should.ThrowAsync<ArgumentException>(() => ReplaceAsync(
			session,
			"Library.Greeter.Greet",
			"public string Greet(string name) => name;")).OfExactType();

		error.Message.ShouldContain("matches 2 declarations", Case.Sensitive);
		error.Message.ShouldContain("Greet(string name)", Case.Sensitive);
		error.Message.ShouldContain("Greet(string title, string name)", Case.Sensitive);
		error.Message.ShouldContain("parameter types", Case.Sensitive);

		var settled = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string, string)",
			"public string Greet(string title, string name) => $\"{_prefix}, {title}. {name}.\";");

		settled.Applied.ShouldBeTrue();
		settled.Symbol.ShouldBe("string Library.Greeter.Greet(string title, string name)");
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

		kept.Notices.ShouldContain(notice => notice.Contains("Kept the comment", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("\t/// <summary>The greeting for one name.</summary>\r\n\tpublic string Greet(string name) => name;", Case.Sensitive);

		var replaced = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"/// <summary>Now documented differently.</summary>\npublic string Greet(string name) => name.Trim();");

		replaced.Notices.ShouldNotContain(notice => notice.Contains("Kept the comment", StringComparison.Ordinal));

		text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("Now documented differently", Case.Sensitive);
		text.ShouldNotContain("The greeting for one name", Case.Sensitive);
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

		statements.Applied.ShouldBeTrue();
		statements.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("\tpublic string Greet(string name)\r\n\t{\r\n\t\treturn name.Trim();\r\n\t}\r\n", Case.Sensitive);
		text.ShouldContain("/// <summary>The greeting for one name.</summary>", Case.Sensitive);

		// An expression body against a member that had a block: the signature is the same either way.
		var arrow = await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Shout(string)", "=> text.ToLowerInvariant();"));

		arrow.Applied.ShouldBeTrue();

		text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("\tprivate static string Shout(string text) => text.ToLowerInvariant();\r\n", Case.Sensitive);
	}

	/// <summary>A member with more than one body is not guessed at.</summary>
	[Test]
	public async Task Declines_a_body_where_there_is_not_exactly_one()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(() => EditAsync(
			session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Count", "=> 3;"))).OfExactType();

		error.Message.ShouldContain("has no body", Case.Sensitive);
		error.Message.ShouldContain("accessors", Case.Sensitive);
		error.Message.ShouldContain("rose_replace_member", Case.Sensitive);
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

		added.Applied.ShouldBeTrue();
		added.Members.ShouldBe(["Loud", "Emphasise"]);
		added.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Greeter.cs");

		// A blank line either side, tab-indented, and between PrefixLength and Count.
		text.ShouldContain(
			"\tpublic int PrefixLength => _prefix.Length;\r\n"
				+ "\r\n\t/// <summary>How loud to be.</summary>\r\n\tpublic bool Loud { get; set; }\r\n"
				+ "\r\n\tpublic string Emphasise(string text) => Loud ? text.ToUpperInvariant() : text;\r\n"
				+ "\r\n\tpublic int Count { get; set; }\r\n", Case.Sensitive);

		// And at the end, when nothing says otherwise.
		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "private const int Limit = 10;",
		});

		text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldEndWith("\r\n\tprivate const int Limit = 10;\r\n}\r\n", Case.Sensitive);
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

		text.ShouldContain("public sealed class Empty\r\n{\r\n\tpublic int Value => 1;\r\n}\r\n", Case.Sensitive);
	}

	[Test]
	public async Task Refuses_a_member_the_type_already_declares()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(() => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public int Count { get; set; }",
		})).OfExactType();

		error.Message.ShouldContain("already declares Count", Case.Sensitive);
		error.Message.ShouldContain("rose_replace_member", Case.Sensitive);
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

		var partial = await Should.ThrowAsync<ArgumentException>(() => EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Split",
			Code = "public int Third => 3;",
		})).OfExactType();

		partial.Message.ShouldContain("matches 2 declarations", Case.Sensitive);
		partial.Message.ShouldContain("Split.cs", Case.Sensitive);
		partial.Message.ShouldContain("SplitAgain.cs", Case.Sensitive);
		partial.Message.ShouldContain("filePath", Case.Sensitive);

		var settled = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Split",
			Code = "public int Third => 3;",
			FilePath = fixture.Path("Members", "Library", "SplitAgain.cs"),
		});

		settled.Applied.ShouldBeTrue();
		(await ReadAsync(fixture, "SplitAgain.cs")).ShouldContain("Third", Case.Sensitive);
		(await ReadAsync(fixture, "Split.cs")).ShouldNotContain("Third", Case.Sensitive);
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

		appended.Applied.ShouldBeTrue();
		appended.Members.ShouldBe(["Blue"]);
		appended.IntroducedDiagnostics.ShouldBeEmpty();

		// In front of a value with an initialiser of its own, so nothing after it is renumbered.
		var between = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Colour",
			After = "Green",
			Code = "Yellow = 7,",
		});

		between.Applied.ShouldBeTrue();
		between.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Kinds.cs");

		text.ShouldContain(
			"{\r\n\tRed,\r\n\r\n\tGreen,\r\n\r\n\tYellow = 7,\r\n\r\n\tBlue = 5,\r\n}\r\n", Case.Sensitive);
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

		appended.Applied.ShouldBeTrue();
		appended.IntroducedDiagnostics.ShouldBeEmpty();

		var inserted = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Access",
			Before = "Read",
			Code = "/// <summary>May read and write.</summary>\nReadWrite = Read | Write,",
		});

		inserted.Applied.ShouldBeTrue();
		inserted.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Access.cs");

		text.ShouldContain(
			"{\r\n"
				+ "\t/// <summary>Nothing at all.</summary>\r\n\tNone = 0,\r\n"
				+ "\t/// <summary>May read and write.</summary>\r\n\tReadWrite = Read | Write,\r\n"
				+ "\t/// <summary>May read.</summary>\r\n\tRead = 1,\r\n"
				+ "\t/// <summary>May write.</summary>\r\n\tWrite = 2,\r\n"
				+ "\t/// <summary>May run.</summary>\r\n\tExecute = 4\r\n"
				+ "}\r\n", Case.Sensitive);
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

		var name = await Should.ThrowAsync<ArgumentException>(() => Add("Green = 3")).OfExactType();
		name.Message.ShouldContain("already declares Green", Case.Sensitive);

		var anchor = await Should.ThrowAsync<ArgumentException>(() => Add("Blue = 9", after: "Purple")).OfExactType();
		anchor.Message.ShouldContain("no value called 'Purple'", Case.Sensitive);
		anchor.Message.ShouldContain("Red, Green", Case.Sensitive);

		(await ReadAsync(fixture, "Kinds.cs")).ShouldBe(original);

		var alias = await Add("Crimson = Red");

		alias.Applied.ShouldBeTrue();
		alias.Notices.ShouldNotContain(notice => notice.Contains("cannot be told apart", StringComparison.Ordinal));

		var value = await Add("Blue = 1");

		value.Applied.ShouldBeTrue();
		value.Notices.ShouldContain(notice => notice.Contains("Blue is 1, which Green already is", StringComparison.Ordinal));

		var renumbered = await Add("Purple", after: "Red");

		renumbered.Applied.ShouldBeTrue();
		renumbered.Notices.ShouldContain(notice => notice.Contains("renumbers Green from 1 to 2", StringComparison.Ordinal));

		(await ReadAsync(fixture, "Kinds.cs")).ShouldContain(
			"{\r\n\tRed,\r\n\r\n\tPurple,\r\n\r\n\tGreen,\r\n\r\n\tCrimson = Red,\r\n\r\n\tBlue = 1,\r\n}", Case.Sensitive);
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

		medium.Applied.ShouldBeTrue();
		medium.IntroducedDiagnostics.ShouldBeEmpty();
		medium.Notices.ShouldNotContain(notice => notice.Contains("renumbers", StringComparison.Ordinal));

		(await ReadAsync(fixture, "Priority.cs")).ShouldContain(
			"{\r\n\tLow = 0x01,\r\n\tMedium = 0x08,\r\n\tHigh = 1 << 4,\r\n\tHighest = byte.MaxValue,\r\n}", Case.Sensitive);

		var urgent = await Add("Urgent = 0x10");

		urgent.Applied.ShouldBeTrue();
		urgent.Notices.ShouldContain(notice => notice.Contains("Urgent is 16, which High already is", StringComparison.Ordinal));

		var over = await Add("Over = 0x100");

		over.Applied.ShouldBeTrue();
		over.IntroducedDiagnostics.ShouldContain(diagnostic => diagnostic.Id == "CS0031");
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

		// Both carry the type a read uses to answer from metadata instead. The second is why it is not
		// enough for that type to mean "no such name": 'Shout' is declared in this solution, just not
		// at the address asked for, and a library member addressed that way is the common case rather
		// than the exotic one.
		var missing = await Should.ThrowAsync<SymbolNotFoundException>(() => ReplaceAsync(
			session, "Library.Greeter.Salute", "public string Salute() => _prefix;")).OfExactType();

		missing.Message.ShouldContain("Nothing in the solution is called 'Salute'", Case.Sensitive);
		missing.Message.ShouldContain("rose_search_symbols", Case.Sensitive);

		var elsewhere = await Should.ThrowAsync<SymbolNotFoundException>(() => ReplaceAsync(
			session, "Library.Caller.Shout(string)", "private static string Shout(string text) => text;")).OfExactType();

		elsewhere.Message.ShouldContain("Nothing is declared at 'Library.Caller.Shout(string)'", Case.Sensitive);
		elsewhere.Message.ShouldContain("Library.Greeter.Shout", Case.Sensitive);
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

		result.Applied.ShouldBeTrue();

		var after = await ReadAsync(fixture, "Greeter.cs");

		// Comment above attribute above declaration, each on its own line at the file's indentation:
		// the comment is content the caller did not supply, and the attribute is now the first token.
		after.ShouldContain(
			"\t/// <summary>Louder.</summary>\r\n\t[Obsolete(\"Shout is going away.\")]\r\n\tprivate static string Shout(string text)", Case.Sensitive);

		after.ShouldContain("ToUpperInvariant() + \"!\"", Case.Sensitive);

		// Named rather than counted, so a caller can tell whether the one it cares about survived.
		result.Notices.ShouldContain(notice => notice.Contains("[Obsolete]", StringComparison.Ordinal));
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

		result.Applied.ShouldBeTrue();

		var after = await ReadAsync(fixture, "Greeter.cs");

		after.ShouldContain("[Obsolete(\"Use Announce instead.\")]", Case.Sensitive);
		after.ShouldNotContain("Shout is going away.", Case.Sensitive);
		result.Notices.ShouldNotContain(notice => notice.Contains("Kept [Obsolete]", StringComparison.Ordinal));
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

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldContain(notice => notice.Contains("imported System.Text", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("using System.Text;", Case.Sensitive);
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

		result.Notices.ShouldContain(
			notice => notice.Contains("Palette is in 2 namespaces", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldNotContain("using Library.Left;", Case.Sensitive);
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

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Members.ShouldBe(["Thrice"]);

		var text = await ReadAsync(fixture, "Regioned.cs");

		text.ShouldNotContain("Thrice", Case.Sensitive);
		text.ShouldNotContain("Trebles it", Case.Sensitive);
		text.ShouldContain("Twice", Case.Sensitive);

		// The region survives, balanced. Cutting a line range takes one half of a pair and leaves the
		// file with CS1024 or CS1028, which is the class of failure this exists to remove.
		text.ShouldContain("#region Helpers", Case.Sensitive);
		text.ShouldContain("#endregion", Case.Sensitive);
		text.ShouldNotContain("\r\n\r\n\r\n", Case.Sensitive);
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

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.Delete,
				Symbol = "Library.Greeter.Greet",
			})).OfExactType();

		thrown.Message.ShouldContain("matches 2 declarations", Case.Sensitive);
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
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

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldNotContain("string title", Case.Sensitive);
		text.ShouldContain("public string Greet(string name)", Case.Sensitive);
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

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Kinds.cs");

		text.ShouldContain("public interface IShape", Case.Sensitive);
		text.ShouldNotContain("double Area()", Case.Sensitive);
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

		result.IntroducedDiagnostics.ShouldContain(entry => entry.Id == "CS0246");

		result.Notices.ShouldContain(
			notice => notice.Contains("Shouted", StringComparison.Ordinal)
				&& notice.Contains("Library.Extras", StringComparison.Ordinal)
				&& notice.Contains("did not resolve", StringComparison.Ordinal));
	}
}
