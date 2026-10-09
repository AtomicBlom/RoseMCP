using static RoseMcp.IntegrationTests.MemberEdits;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Half of what writing C# by symbol promises: code arriving in some other shape ends up in the
/// file's. The fixture is a repository with opinions of its own -- tabs, CRLF, a final newline --
/// because a fixture formatted like this repository could not tell the difference.
/// <para>
/// What is asserted here is the file on disk, never the result, since a result claiming success over
/// a mangled file is the failure being designed out. Wrapping, indentation, expression bodies and
/// literals each get their own test because each is a different way for a rewrite to be correct and
/// unusable.
/// </para>
/// </summary>
public sealed class MemberEditFormattingTests
{
	/// <summary>
	/// Code written the way a caller writes it -- four spaces, bare newlines -- landing in a file
	/// that wants tabs and CRLF. In a repository escalating IDE0055 the difference is a failed
	/// build, and it is the single most common thing a text edit gets wrong.
	/// </summary>
	[Test]
	public async Task Writes_a_member_in_the_formatting_the_file_asks_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name)\n{\n    return $\"{_prefix} there, {name}!\";\n}");

		result.Applied.ShouldBeTrue();
		result.Symbol.ShouldBe("string Library.Greeter.Greet(string name)");
		result.Members.ShouldBeNull("the symbol already names the one member written");

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("\t\treturn $\"{_prefix} there, {name}!\";\r\n", Case.Sensitive);
		text.ShouldNotContain("    return", Case.Sensitive);
		text.Replace("\r\n", string.Empty, StringComparison.Ordinal).ShouldNotContain("\n", Case.Sensitive);
		text.ShouldEndWith("}\r\n", Case.Sensitive);
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
	[Test]
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
		text.ShouldContain(
			"\tpublic string Greet(\r\n\t\tstring title,\r\n\t\tstring name)\r\n\t{\r\n", Case.Sensitive);
	}

	/// <summary>
	/// The same rule on the way in as on the way over: a member added with a hand-wrapped parameter
	/// list keeps the shape the caller gave it and lands at the destination's own level, whether they
	/// wrote the whole thing at column zero, already indented for where it goes, or opening with a
	/// line break.
	/// <para>
	/// A whole member carries its own first line, so the relative shape the caller wrote is the
	/// specification and the baseline is all that has to come off. That is what separates this from a
	/// bare parameter list, which opens after the parenthesis with no first line to measure and so
	/// takes its level from the declaration instead -- and it is why a member whose text opens with a
	/// line break still starts at the splice point rather than a line below it.
	/// </para>
	/// </summary>
	[Test]
	[Arguments("public string Wrapped(\n\tstring first,\n\tstring second) => first + second;")]
	[Arguments("\tpublic string Wrapped(\n\t\tstring first,\n\t\tstring second) => first + second;")]
	[Arguments("\t\tpublic string Wrapped(\n\t\t\tstring first,\n\t\t\tstring second) => first + second;")]
	[Arguments("\npublic string Wrapped(\n\tstring first,\n\tstring second) => first + second;")]
	[Arguments("\n\t\tpublic string Wrapped(\n\t\t\tstring first,\n\t\t\tstring second) => first + second;")]
	public async Task Lines_up_a_wrapped_parameter_list_on_a_member_it_adds(string written)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = written,
		});

		var text = await ReadAsync(fixture, "Greeter.cs");

		// One tab for the member, two for the parameters it wrapped onto their own lines.
		text.ShouldContain(
			"\tpublic string Wrapped(\r\n\t\tstring first,\r\n\t\tstring second) => first + second;", Case.Sensitive);
	}

	/// <summary>
	/// The third site the same indentation rule runs at: a multi-line replacement spliced into a
	/// body. It lands at the indentation of the line the match starts on, and the shape the caller
	/// wrote is what decides the rest -- flat, or opening with a line break, are one request, because
	/// the line exempted from the shift is the first one with content on it rather than the first one
	/// there is.
	/// <para>
	/// A replacement is measured against its own first line and not against the file, so writing the
	/// continuations at the depth they will end up at while leaving the first line flush asks for that
	/// depth again on top. Nothing downstream says so, which is why it is worth a case: a continuation
	/// line is not a statement, so Roslyn's formatter has no rule that moves one back.
	/// </para>
	/// </summary>
	[Test]
	[Arguments("return first\n\t+ second\n\t+ third;")]
	[Arguments("\nreturn first\n\t+ second\n\t+ third;")]
	public async Task Lines_up_a_wrapped_replacement_inside_a_body(string replace)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Wrapped.Join",
			Find = "return first + second + third;",
			Replace = replace,
		});

		var text = await ReadAsync(fixture, "Wrapped.cs");

		// Two tabs for the statement, three for the lines it wraps onto.
		text.ShouldContain(
			"\t\treturn first\r\n\t\t\t+ second\r\n\t\t\t+ third;", Case.Sensitive);
	}

	/// <summary>
	/// An expression body wrapped onto its own line, replaced whole. The <c>=&gt;</c> and what follows
	/// it are a continuation rather than a statement, so Roslyn's formatter has no rule that puts one
	/// back where it belongs and neither IDE0055 nor <c>dotnet format</c> has an opinion about where
	/// it sits.
	/// </summary>
	[Test]
	public async Task Keeps_an_expression_body_a_level_in_when_it_replaces_the_member()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Library.Arrowed.Describe",
			Code = "public static string Describe(string first, string second) =>\n\tfirst + \" and \" + second;",
		});

		var text = await ReadAsync(fixture, "Arrowed.cs");

		text.ShouldContain(
			"\tpublic static string Describe(string first, string second) =>\r\n\t\tfirst + \" and \" + second;", Case.Sensitive);
	}

	/// <summary>
	/// The same body supplied as a body rather than as a member, which is the path that copies the
	/// signature out of the file and measures the caller's code against what follows it.
	/// </summary>
	[Test]
	public async Task Keeps_an_expression_body_a_level_in_when_it_replaces_the_body()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Arrowed.Describe",
			Code = "=>\n\tfirst + \" and \" + second;",
		});

		var text = await ReadAsync(fixture, "Arrowed.cs");

		text.ShouldContain(
			"\tpublic static string Describe(string first, string second) =>\r\n\t\tfirst + \" and \" + second;", Case.Sensitive);
	}

	/// <summary>
	/// A find and replace inside an expression body, which is the shape the finding names: the body
	/// came out a level shallower than it went in and <c>rose_format</c> called the file formatted.
	/// </summary>
	[Test]
	public async Task Leaves_an_expression_body_where_it_was_on_a_find_and_replace()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Arrowed.Spread",
			Find = "+ \", \" + third",
			Replace = "+ \"; \" + third",
		});

		var text = await ReadAsync(fixture, "Arrowed.cs");

		// Two tabs for the body's first line, three for the lines it wraps onto.
		text.ShouldContain(
			"\t\tfirst\r\n\t\t\t+ \", \" + second\r\n\t\t\t+ \"; \" + third;", Case.Sensitive);
	}

	/// <summary>
	/// A value wrapped below its "=" stays below it, whether the whole value is written or part of it is
	/// found and replaced. Joined to the declaration with a space, it was pulled up onto the declaration's
	/// line -- and a collection expression's "[" with it -- which no formatting rule puts back and no
	/// request asked for. A caller who starts a value with a line break is asking for the wrap, and gets
	/// it where the file had none.
	/// </summary>
	[Test]
	[Arguments("Long", "\"replaced\"", null, "\tpublic const string Long =\r\n\t\t\"replaced\";\r\n")]
	[Arguments("Names", null, "\"two\",", "\tpublic static readonly string[] Names =\r\n\t[\r\n\t\t\"one\",\r\n\t\t\"three\",\r\n\t];\r\n")]
	[Arguments("Short", "\n\"wrapped\"", null, "\tpublic const string Short =\r\n\t\t\"wrapped\";\r\n")]
	public async Task Keeps_a_value_on_the_line_it_was_on(string member, string? code, string? find, string expected)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", "Values.cs"),
			"""
			namespace Library;

			public static class Values
			{
				public const string Long =
					"a value long enough that it was wrapped onto a line of its own";

				public static readonly string[] Names =
				[
					"one",
					"two",
				];

				public const string Short = "a";
			}

			""".ReplaceLineEndings("\r\n"),
			TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = $"Library.Values.{member}",
			Code = code ?? string.Empty,
			Find = find,
			Replace = find is null ? null : "\"three\",",
		});

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldNotContain(
			notice => notice.Contains("Nothing this was asked to do reaches them", StringComparison.Ordinal));

		(await ReadAsync(fixture, "Values.cs")).ShouldContain(expected, Case.Sensitive);
	}

	/// <summary>
	/// The lines beside a write are the file's, however untidy: trailing spaces after the member above, a
	/// blank line holding a tab, a neighbour indented with spaces. Formatting what was written reached
	/// into the whitespace either side of it and tidied all three, which the overreach sentence then had
	/// to name -- a write that changes lines it was not asked about, reported or not, is a diff nobody
	/// can review as the change they made.
	/// </summary>
	[Test]
	[Arguments("add")]
	[Arguments("body")]
	[Arguments("find")]
	public async Task Leaves_the_untidy_lines_beside_a_write_alone(string kind)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		var original =
			"namespace Library;\r\n\r\npublic sealed class Untidy\r\n{\r\n"
				+ "\tpublic int First => 1;  \r\n"
				+ "\t\r\n"
				+ "\t/// <summary>Counts. </summary>\r\n"
				+ "\tpublic int Count()\r\n\t{\r\n\t\tvar total = 1;\r\n\t\t\r\n\t\treturn total;\r\n\t}\r\n"
				+ "\t\r\n"
				+ "    public int Second { get; set; }\r\n"
				+ "}\r\n";

		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", "Untidy.cs"), original, TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var request = kind switch
		{
			"add" => new MemberEditRequest
			{
				Kind = MemberEditKind.Add,
				Symbol = "Library.Untidy",
				Code = "public int Added => 2;",
				After = "First",
			},
			"body" => new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Untidy.Count",
				Code = "var total = 2;\n\nreturn total;",
			},
			_ => new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Untidy.Count",
				Find = "return total;",
				Replace = "return total + 1;",
			},
		};

		var result = await EditAsync(session, request);

		result.Applied.ShouldBeTrue();
		result.Notices
			.Where(notice => notice.Contains("Nothing this was asked to do reaches them", StringComparison.Ordinal))
			.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Untidy.cs");

		text.ShouldContain("\tpublic int First => 1;  \r\n", Case.Sensitive);
		text.ShouldContain("\t/// <summary>Counts. </summary>\r\n", Case.Sensitive);
		text.ShouldContain("\r\n\t\r\n    public int Second { get; set; }\r\n", Case.Sensitive);
	}

	/// <summary>
	/// An expression-bodied member added rather than edited, with its body on a line of its own.
	/// </summary>
	[Test]
	public async Task Keeps_an_expression_body_a_level_in_on_a_member_it_adds()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Arrowed",
			Code = "public static string Joined(string first, string second) =>\n\tfirst + second;",
		});

		var text = await ReadAsync(fixture, "Arrowed.cs");

		text.ShouldContain(
			"\tpublic static string Joined(string first, string second) =>\r\n\t\tfirst + second;", Case.Sensitive);
	}

	/// <summary>
	/// The same shift must not reach inside a verbatim string. Its leading whitespace is part of the
	/// value, and no delimiter rule takes it back out again, so a literal written flush left stays
	/// flush left however deep the member around it sits.
	/// </summary>
	[Test]
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
		text.ShouldContain("@\"\r\nflush left on purpose\r\n\";", Case.Sensitive);
	}

	/// <summary>
	/// A multi-line literal composed for a JSON argument arrives with bare newlines, which in a CRLF
	/// file fails dotnet format while no build complains and the obvious fix changes what the program
	/// says. The endings become the file's, and the result says so and names the literal's line, because
	/// a diff cannot show a terminator and this one is part of a string's value.
	/// <para>
	/// A caller that writes a carriage return is thinking about endings, and then nothing is touched --
	/// which is also the way to ask for a bare newline inside a literal on purpose.
	/// </para>
	/// </summary>
	[Test]
	public async Task Rewrites_the_endings_of_a_literal_composed_without_them()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var bare = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "/// <summary>Two lines.</summary>\npublic string Bare() => @\"\nline one\nline two\n\";",
		});

		bare.Notices.ShouldContain(
			notice => notice.StartsWith("Rewrote 3 line ending(s) to CRLF", StringComparison.Ordinal)
				&& notice.Contains("literal on line 2 of the code supplied", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("@\"\r\nline one\r\nline two\r\n\";", Case.Sensitive);

		// Supplied with the file's own endings, there is nothing to rewrite and nothing to say.
		var matching = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Matching() => @\"\r\nline one\r\nline two\r\n\";",
		});

		matching.Notices.ShouldNotContain(notice => notice.StartsWith("Rewrote", StringComparison.Ordinal));
	}

	/// <summary>
	/// Code holding no multi-line literal arrives with bare newlines on every call, and they become the
	/// file's without a word: outside a literal an ending is layout, and a sentence on every write says
	/// nothing a caller can act on while teaching them to skim the notices that matter.
	/// </summary>
	[Test]
	public async Task Rewrites_the_endings_of_code_without_a_literal_in_silence()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "/// <summary>Says it twice.</summary>\npublic string Twice(string name)\n{\n\tvar once = Greet(name);\n\treturn once + \" \" + once;\n}",
		});

		result.Notices.ShouldNotContain(notice => notice.Contains("line ending", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("\tpublic string Twice(string name)\r\n\t{\r\n\t\tvar once = Greet(name);\r\n", Case.Sensitive);
	}

	/// <summary>
	/// A member edit rewrites the member and not the file. A repository whose endings are already
	/// inconsistent would otherwise get every line rewritten by a one-member change, which buries
	/// the edit in a diff nobody can review.
	/// </summary>
	[Test]
	public async Task Leaves_the_lines_it_did_not_write_exactly_as_they_were()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var original = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		// The whole file given the wrong endings behind the workspace's back, as a stray tool would.
		await File.WriteAllTextAsync(
			path,
			original.Replace("\r\n", "\n", StringComparison.Ordinal),
			TestContext.Current!.Execution.CancellationToken);

		await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Greet(string)", "return name.Trim();"));

		var text = await ReadAsync(fixture, "Greeter.cs");

		// The member that was written obeys .editorconfig ...
		text.ShouldContain("\t/// <summary>The greeting for one name.</summary>\r\n", Case.Sensitive);
		text.ShouldContain("\t\treturn name.Trim();\r\n", Case.Sensitive);

		// ... and every member that was not is still exactly as it was found, bare newlines and all.
		text.ShouldContain("private readonly string _prefix = \"Hello\";\n", Case.Sensitive);
		text.ShouldContain("public int PrefixLength => _prefix.Length;\n", Case.Sensitive);
		text.ShouldContain("return text.ToUpperInvariant();\n", Case.Sensitive);
		text.ShouldEndWith("}\n", Case.Sensitive);

		// The rest of the file is untouched, the blank line above the member included: only the
		// member's own five lines -- its comment, signature, braces and statement -- were rewritten,
		// which is what keeps a one-member change reviewable.
		var normalised = text.Split("\r\n").Length - 1;

		normalised.ShouldBe(5);
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
	[Test]
	public async Task Leaves_a_wrapped_signature_exactly_as_it_was()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(
			session,
			Request(MemberEditKind.ReplaceBody, "Library.Wrapped.Join", "return string.Concat(first, second, third);"));

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Wrapped.cs");

		text.ShouldContain(
			"\tpublic static string Join(\r\n\t\tstring first,\r\n\t\tstring second,\r\n\t\tstring third)\r\n", Case.Sensitive);
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
	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	public async Task Keeps_a_wrapped_call_in_a_body_a_level_in(int written)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var baseline = new string('\t', written);

		var code = $"{baseline}return string.Concat(\n{baseline}\tfirst,\n{baseline}\tsecond,\n{baseline}\tthird);";

		var result = await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Library.Wrapped.Join", code));

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Wrapped.cs");

		text.ShouldContain(
			"\t{\r\n\t\treturn string.Concat(\r\n\t\t\tfirst,\r\n\t\t\tsecond,\r\n\t\t\tthird);\r\n\t}", Case.Sensitive);
	}

	/// <summary>
	/// An anchored replacement keeps its own shape, exactly as a whole body does. The replacement
	/// arrives in the caller's coordinate system and is spliced into a body written in the file's, so
	/// without the baseline pass the two indentations add up and every line the caller wrapped by hand
	/// lands that much further in -- silently, since a continuation line is not a statement and the
	/// formatter has no rule that moves one back.
	/// </summary>
	[Test]
	[Arguments(0)]
	[Arguments(2)]
	public async Task Keeps_the_shape_of_an_anchored_replacement(int written)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var baseline = new string('\t', written);

		var replace = $"{baseline}return string.Concat(\n{baseline}\tfirst,\n{baseline}\tsecond,\n{baseline}\tthird);";

		var result = await EditAsync(
			session,
			new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Wrapped.Join",
				Find = "return first + second + third;",
				Replace = replace,
			});

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Wrapped.cs");

		text.ShouldContain(
			"\t{\r\n\t\treturn string.Concat(\r\n\t\t\tfirst,\r\n\t\t\tsecond,\r\n\t\t\tthird);\r\n\t}", Case.Sensitive);
	}

	/// <summary>
	/// The same pass must not reach inside a verbatim literal the replacement carries. Its leading
	/// whitespace is the value, so a line of it moved is a changed string rather than changed layout,
	/// and nothing downstream reports it.
	/// </summary>
	[Test]
	public async Task Leaves_a_literal_in_an_anchored_replacement_alone()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var replace = "\t\tvar banner = @\"one\r\ntwo\";\r\n\t\treturn banner + first + second + third;";

		var result = await EditAsync(
			session,
			new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Wrapped.Join",
				Find = "return first + second + third;",
				Replace = replace,
			});

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Wrapped.cs");

		text.ShouldContain("@\"one\r\ntwo\";", Case.Sensitive);
	}
}
