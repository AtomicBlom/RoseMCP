using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Creating a file. The first thing most work does is start a class, so this is the earliest point
/// at which a session can stop being able to ask semantic questions: a file written outside the
/// workspace leaves it mid-edit, and from there a build is being paid for anyway.
/// </summary>
public sealed class AddFileTests
{
	[Test]
	public async Task Writes_declarations_under_a_namespace_the_folder_implies()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Nested", "Badge.cs");

		var result = await AddAsync(session, path, "public sealed class Badge\n{\n    public string Text => \"hi\";\n}");

		result.Applied.ShouldBeTrue();
		result.Project.ShouldBe("Library");
		result.Namespace.ShouldBe("Library.Nested");
		result.Types.ShouldBe(["Badge"]);
		result.InTheBuild.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		// The file's conventions, not the caller's: tabs where four spaces arrived, CRLF where bare
		// newlines did, and a file-scoped namespace.
		text.ShouldContain("namespace Library.Nested;\r\n", Case.Sensitive);
		text.ShouldContain("\tpublic string Text => \"hi\";\r\n", Case.Sensitive);
		text.ShouldNotContain("    ", Case.Sensitive);
	}

	/// <summary>
	/// A repository that settles its endings in .gitattributes and says nothing of them in .editorconfig. The
	/// new file's lines end the way git writes them, whatever the code arrived with -- and whatever the files
	/// beside it hold, which here is CRLF, so it is visibly the attributes that decided.
	/// </summary>
	[Test]
	public async Task Ends_a_new_file_s_lines_as_gitattributes_says_where_no_editorconfig_does()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
		await File.WriteAllTextAsync(
			Path.Combine(fixture.Root, ".gitattributes"), "*.cs text eol=lf\n", TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Simple", "Core", "Shape.cs");
		var result = await AddAsync(session, path, "namespace Core;\r\n\r\npublic sealed class Shape\r\n{\r\n    public int Sides => 3;\r\n}\r\n");

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		// The indentation is the files' beside it, since nothing declares that either.
		text.ShouldNotContain("\r", Case.Sensitive);
		text.ShouldContain("\n\tpublic int Sides => 3;\n", Case.Sensitive);
	}

	/// <summary>
	/// An .editorconfig the workspace was never given: the design-time build lists only the ones above a file
	/// it compiles, so one in a folder that held no source when the workspace loaded is unknown to Roslyn. It
	/// is the one dotnet format will read, so a new file there follows it rather than Roslyn's defaults or the
	/// tabs and CRLF of the files beside it.
	/// </summary>
	[Test]
	public async Task Follows_an_editorconfig_the_workspace_was_never_given()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var directory = fixture.Path("Simple", "Core", "Shapes");
		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(
			Path.Combine(directory, ".editorconfig"),
			"[*.cs]\nindent_style = space\nindent_size = 2\nend_of_line = lf\n",
			TestContext.Current!.Execution.CancellationToken);

		var path = Path.Combine(directory, "Square.cs");
		var result = await AddAsync(session, path, "namespace Core.Shapes;\n\npublic sealed class Square\n{\n\tpublic int Sides => 4;\n}\n");

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldNotContain("\r", Case.Sensitive);
		text.ShouldContain("\n  public int Sides => 4;\n", Case.Sensitive);
	}

	/// <summary>
	/// A whole file the caller supplies keeps the layout they gave it, except where a rule the
	/// repository enforces applies. Blank lines between using groups, between members and inside a
	/// body are structure a reader put there on purpose; a wrapped chain is a line-length decision
	/// nothing here is entitled to overturn; and the spacing inside a documentation tag is not
	/// whitespace the language has an opinion about.
	/// <para>
	/// All four came from regenerating the trivia wholesale rather than formatting what arrived,
	/// which is the one operation guaranteed to lose every one of them at once.
	/// </para>
	/// </summary>
	[Test]
	public async Task Keeps_the_layout_of_a_whole_file_it_is_given()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Kept.cs");

		var result = await AddAsync(
			session,
			path,
			"using System;\n"
				+ "\n"
				+ "using System.Linq;\n"
				+ "\n"
				+ "namespace Library;\n"
				+ "\n"
				+ "/// <summary>Kept as written.</summary>\n"
				+ "public static class Kept\n"
				+ "{\n"
				+ "\t/// <summary>Counts the positive values.</summary>\n"
				+ "\t/// <param name=\"values\">The values to count.</param>\n"
				+ "\tpublic static int Count(int[] values)\n"
				+ "\t{\n"
				+ "\t\tif (values is null)\n"
				+ "\t\t{\n"
				+ "\t\t\treturn 0;\n"
				+ "\t\t}\n"
				+ "\n"
				+ "\t\treturn values\n"
				+ "\t\t\t.Where(value => value > 0)\n"
				+ "\t\t\t.Select(value => value * 2)\n"
				+ "\t\t\t.Count();\n"
				+ "\t}\n"
				+ "\n"
				+ "\tpublic static string Describe() => nameof(Kept);\n"
				+ "}\n");

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		// The blank line between the two using groups, and the one below the namespace.
		text.ShouldContain("using System;\r\n\r\nusing System.Linq;\r\n", Case.Sensitive);
		text.ShouldContain("namespace Library;\r\n\r\n/// <summary>Kept as written.</summary>", Case.Sensitive);

		// The documentation tag as written, not respaced around the equals sign.
		text.ShouldContain("<param name=\"values\">", Case.Sensitive);

		// The braces the caller wrote, and the blank line after them.
		text.ShouldContain("\t\tif (values is null)\r\n\t\t{\r\n\t\t\treturn 0;\r\n\t\t}\r\n\r\n", Case.Sensitive);

		// The chain still wrapped, one call to a line.
		text.ShouldContain(
			"\t\treturn values\r\n\t\t\t.Where(value => value > 0)\r\n\t\t\t.Select(value => value * 2)\r\n\t\t\t.Count();", Case.Sensitive);

		// And the blank line between the two members.
		text.ShouldContain("\t}\r\n\r\n\tpublic static string Describe()", Case.Sensitive);
	}

	/// <summary>
	/// A namespace the code declares is kept, because there are real reasons to want one that does
	/// not match the folder -- and said out loud, because IDE0130 is a build error where it is
	/// turned up and the caller may not have meant it.
	/// </summary>
	[Test]
	public async Task Keeps_a_namespace_the_code_declares_and_says_it_disagrees()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Nested", "Token.cs");

		var result = await AddAsync(session, path, "namespace Library;\n\npublic sealed class Token;");

		result.Namespace.ShouldBe("Library");
		result.Notices.ShouldContain(
			notice => notice.Contains("the folder implies Library.Nested", StringComparison.Ordinal));
	}

	/// <summary>
	/// The imports the code needs are worked out from the compilation that was built to check it,
	/// and the ones with a single answer are added. Reporting the namespace and stopping is a round
	/// trip at exactly the moment the caller was promised there would not be one.
	/// </summary>
	[Test]
	public async Task Adds_the_import_a_single_answer_settles()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Encoder.cs");

		var result = await AddAsync(
			session,
			path,
			"public static class Encoder\n{\n    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);\n}");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.ImportsAdded.ShouldContain(line => line.Contains("System.Text", StringComparison.Ordinal));

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldContain("using System.Text;", Case.Sensitive);
	}

	/// <summary>
	/// A name in two namespaces is reported rather than resolved. The wrong import compiles and
	/// binds to the wrong type, which is the failure with no symptom at all.
	/// </summary>
	[Test]
	public async Task Refuses_to_choose_between_two_namespaces()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Painter.cs");

		// Palette is declared in Library.Left and Library.Right, which is what the fixture has them
		// for.
		var result = await AddAsync(
			session,
			path,
			"public static class Painter\n{\n    public static string Chosen() => Palette.Name;\n}");

		result.ImportsAmbiguous.ShouldContain(
			line => line.Contains("Palette is in 2 namespaces", StringComparison.Ordinal));

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldNotContain("using Library.Left;", Case.Sensitive);
		text.ShouldNotContain("using Library.Right;", Case.Sensitive);
	}

	[Test]
	public async Task Refuses_a_path_that_already_exists()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var before = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Other;")).OfExactType();

		thrown.Message.ShouldContain("already in the solution", Case.Sensitive);
		(await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken)).ShouldBe(before);
	}

	/// <summary>
	/// Code that does not parse is refused before anything is placed, so a bad call leaves no file
	/// behind -- the same promise every other write here makes.
	/// </summary>
	[Test]
	public async Task Refuses_code_that_does_not_parse()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Broken.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Broken { public void M() { ")).OfExactType();

		thrown.Message.ShouldContain("does not parse", Case.Sensitive);
		File.Exists(path).ShouldBeFalse("a refusal writes nothing");
	}

	/// <summary>
	/// A path outside every project's directory would compile nowhere, and saying so beats writing a
	/// file nothing can see.
	/// </summary>
	[Test]
	public async Task Refuses_a_path_no_project_would_compile()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = Path.Combine(fixture.Path("Members"), "Loose.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Loose;")).OfExactType();

		thrown.Message.ShouldContain("not inside the directory of any project in Members.slnx", Case.Sensitive);
		thrown.Message.ShouldContain("workspace argument", Case.Sensitive);
	}

	/// <summary>A preview writes nothing and still answers the question a preview is asking.</summary>
	[Test]
	public async Task Previews_without_writing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Preview.cs");

		var result = await AddAsync(session, path, "public sealed class Preview;", apply: false);

		result.Applied.ShouldBeFalse("a preview says what it would do without doing it");
		result.Diff.ShouldNotBeEmpty();
		File.Exists(path).ShouldBeFalse("a preview leaves no file behind");
	}

	/// <summary>
	/// A whole file of raw literals is what this tool receives, and the endings inside them are kept
	/// -- an ending inside a literal is part of the string's value. The consequence has to be said: the
	/// file fails dotnet format on an ENDOFLINE inside the literal, no build reports it, and the
	/// obvious fix changes what the program says. Sixty-four such errors landed on one added file with
	/// nothing in the result to mention a single one.
	/// <para>
	/// The code here carries a CR LF, which is what says the caller is thinking about endings, so the
	/// bare LFs inside the literal are left exactly as written rather than rewritten to the file's.
	/// </para>
	/// </summary>
	[Test]
	public async Task Says_when_a_literals_endings_are_not_the_files()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AddAsync(
			session,
			fixture.Path("Members", "Library", "Described.cs"),
			"public static class Described\r\n{\r\n\tpublic const string Text = \"\"\"\nfirst\nsecond\n\"\"\";\r\n}\r\n");

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Described.cs"),
			TestContext.Current!.Execution.CancellationToken);

		// Kept, which is the invariant the notice exists to explain rather than to fix.
		text.ShouldContain("first\nsecond\n", Case.Sensitive);

		result.Notices.ShouldContain(
			notice => notice.Contains("line endings the file does not use", StringComparison.Ordinal)
				&& notice.Contains("dotnet format will still ask for them", StringComparison.Ordinal)
				&& notice.Contains("sent with bare LFs only, its literals take the file's endings", StringComparison.Ordinal));

		result.Notices.ShouldNotContain(
			notice => notice.StartsWith("Rewrote", StringComparison.Ordinal)
				&& notice.Contains("inside the multi-line string literal", StringComparison.Ordinal),
			"code carrying a CR LF is kept as it arrived, so nothing was rewritten");
	}

	/// <summary>
	/// Code sent with bare LFs only, which is what composing it for a JSON argument produces, takes the
	/// file's endings throughout, the inside of its literals included -- as it does through the member
	/// tools -- and says which literals that changed, by their lines in what was sent rather than in the
	/// file the namespace and imports were put around. Nothing is left for dotnet format to ask for, so
	/// the sentence about endings the file does not use has nothing to say.
	/// </summary>
	[Test]
	public async Task Rewrites_the_literals_of_code_sent_with_bare_line_feeds_and_says_where()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Composed.cs");

		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new AddFileRequest
		{
			FilePath = path,
			Code = "public static class Composed\n{\n\tpublic const string Text = \"\"\"\n\t\tfirst\n\t\tsecond\n\t\t\"\"\";\n\n\tpublic static string Joined => string.Join(\",\", new[] { Text });\n}\n",
			Usings = ["System.Globalization"],
		};

		var result = await session.MutateAsync(
			(snapshot, token) => AddFileService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldContain("\"\"\"\r\n\t\tfirst\r\n\t\tsecond\r\n\t\t\"\"\";\r\n", Case.Sensitive);
		text.Replace("\r\n", string.Empty, StringComparison.Ordinal).ShouldNotContain("\n", Case.Sensitive, "Every ending is the file's.");

		result.Notices.ShouldContain(
			"Rewrote 1 line ending(s) to CRLF, the ending this file uses, inside the multi-line string literal on line 3 "
				+ "of the code supplied, which changes its value. Write one CR LF anywhere in the code to keep every "
				+ "ending exactly as it arrived.");

		result.Notices.ShouldNotContain(
			notice => notice.Contains("line endings the file does not use", StringComparison.Ordinal));
	}

	/// <summary>A file whose literals agree with it says nothing, so the notice means something.</summary>
	[Test]
	public async Task Says_nothing_when_a_literals_endings_are_the_files()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AddAsync(
			session,
			fixture.Path("Members", "Library", "Agreed.cs"),
			"public static class Agreed\r\n{\r\n\tpublic const string Text = \"\"\"\r\n\t\tfirst\r\n\t\tsecond\r\n\t\t\"\"\";\r\n}\r\n");

		result.Applied.ShouldBeTrue();

		result.Notices.ShouldNotContain(
			notice => notice.Contains("line endings the file does not use", StringComparison.Ordinal));
	}

	/// <summary>
	/// The imports a new file opens with, System first. They were sorted ordinally, which puts
	/// anything alphabetically before "System" above it -- so a file asking for RoseMcp.Contracts and
	/// System.Text.Json opened with the wrong one. It compiles and trips no analyzer, so the only way
	/// to find it is to look.
	/// </summary>
	[Test]
	public async Task Opens_a_new_file_with_System_imports_first()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new AddFileRequest
		{
			FilePath = fixture.Path("Members", "Library", "Ordered.cs"),
			Code = "public static class Ordered\r\n{\r\n\tpublic static string Name => Marker.Name;\r\n}\r\n",
			Usings = ["Library.Nested", "System.Globalization"],
		};

		var result = await session.MutateAsync(
			(snapshot, token) => AddFileService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Ordered.cs"),
			TestContext.Current!.Execution.CancellationToken);

		text.IndexOf("using System.Globalization;", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("using Library.Nested;", StringComparison.Ordinal),
			$"System did not come first: {text}");
	}

	/// <summary>
	/// The usings argument joins the imports the code declares as one ordered list. Prepended as a
	/// block of its own, a namespace outside System lands above the code's System import with a blank
	/// line between -- which compiles, and fails dotnet format on import ordering.
	/// </summary>
	[Test]
	public async Task Merges_the_usings_argument_into_the_imports_the_code_declares()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Reflected.cs");

		var result = await AddAsync(
			session,
			path,
			"using System.Reflection;\r\n\r\npublic static class Reflected\r\n{\r\n\tpublic static string Name => Marker.Name + typeof(Reflected).GetTypeInfo().Name;\r\n}\r\n",
			usings: ["Library.Nested"]);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldStartWith("using System.Reflection;\r\nusing Library.Nested;\r\n\r\nnamespace Library;\r\n", Case.Sensitive, text);
	}

	/// <summary>
	/// Where the code separates its import groups, each requested import goes into its own group,
	/// in order, and the file keeps its separation -- what rose_add_using does to a file that exists.
	/// </summary>
	[Test]
	public async Task Places_each_requested_using_in_the_group_the_code_already_has()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Grouped.cs");

		var result = await AddAsync(
			session,
			path,
			"using System.Text;\r\n\r\nusing Library.Nested;\r\n\r\npublic static class Grouped\r\n{\r\n\tpublic static string Name => Marker.Name;\r\n}\r\n",
			usings: ["Library.Extras", "System.Globalization"]);

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldStartWith(
			"using System.Globalization;\r\nusing System.Text;\r\n\r\nusing Library.Extras;\r\nusing Library.Nested;\r\n\r\nnamespace Library;\r\n",
			Case.Sensitive,
			text);
	}

	/// <summary>
	/// A requested import the compilation already has -- from the SDK's implicit usings, from the
	/// file's own namespace, or written in the code -- is not written a second time, since that is
	/// IDE0005, and each is named so the caller does not go looking for it.
	/// </summary>
	[Test]
	public async Task Does_not_write_a_requested_using_already_in_scope()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Scoped.cs");

		var result = await AddAsync(
			session,
			path,
			"using System.Text;\r\n\r\npublic static class Scoped\r\n{\r\n\tpublic static Encoding Utf8 => Encoding.UTF8;\r\n}\r\n",
			usings: ["System.Linq", "Library", "System.Text"]);

		result.Applied.ShouldBeTrue();

		result.Notices.ShouldContain("Did not import System.Linq: in scope already, from a global or implicit using.");
		result.Notices.ShouldContain("Did not import Library: in scope already, since this file is in namespace Library.");
		result.Notices.ShouldContain("Did not import System.Text: already imported here.");

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldStartWith("using System.Text;\r\n\r\nnamespace Library;\r\n", Case.Sensitive, text);
	}

	/// <summary>
	/// A requested alias whose name the code already gives to something else does not compile, so it
	/// is refused before anything is written rather than left for the build to find.
	/// </summary>
	[Test]
	public async Task Refuses_a_requested_alias_the_code_already_uses_for_something_else()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Aliased.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => AddAsync(
				session,
				path,
				"using Text = System.Text;\r\n\r\npublic static class Aliased\r\n{\r\n\tpublic static Text.Encoding Utf8 => Text.Encoding.UTF8;\r\n}\r\n",
				usings: ["Text = System.IO"])).OfExactType();

		thrown.Message.ShouldContain("Text already stands for System.Text", Case.Sensitive);
		File.Exists(path).ShouldBeFalse("a refusal writes nothing");
	}

	/// <summary>
	/// Several requested imports into code shorter than they are, which is the ordinary new record.
	/// Each one written grows the file the next is placed into, and the scope of each is still asked
	/// of the file as it was added.
	/// </summary>
	[Test]
	public async Task Writes_several_requested_usings_into_a_one_line_record()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Point.cs");

		var result = await AddAsync(
			session,
			path,
			"public sealed record Point(int X, int Y);",
			usings: ["System.Text.Json", "System.Text.Json.Serialization", "System.Collections.Immutable"]);

		result.Applied.ShouldBeTrue();

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldStartWith(
			"using System.Collections.Immutable;\r\nusing System.Text.Json;\r\nusing System.Text.Json.Serialization;\r\n\r\nnamespace Library;\r\n",
			Case.Sensitive,
			text);
	}

	/// <summary>
	/// Code that keeps its imports inside a namespace block has the requested ones placed there with
	/// them. Placed at file level, one the block already has is written a second time, which is IDE0005,
	/// and any other lands apart from the imports it belongs with.
	/// </summary>
	[Test]
	public async Task Places_requested_usings_inside_the_namespace_block_that_keeps_the_code_s_own()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Blocked.cs");

		var result = await AddAsync(
			session,
			path,
			"namespace Library\r\n{\r\n\tusing System.Text;\r\n\r\n\tpublic static class Blocked\r\n\t{\r\n\t\tpublic static Encoding Utf8 => Encoding.UTF8;\r\n\t}\r\n}\r\n",
			usings: ["System.Text", "System.Globalization"]);

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldContain("Did not import System.Text: already imported here.");

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		text.ShouldStartWith(
			"namespace Library\r\n{\r\n\tusing System.Globalization;\r\n\tusing System.Text;\r\n\r\n\tpublic static class Blocked\r\n",
			Case.Sensitive,
			text);
	}

	/// <summary>
	/// A project that lists its files is worked on in one order: name the file in the project, then create
	/// it. The project then loads a document for the name with nothing on disk behind it, and refusing that
	/// as a file already in the solution leaves no tool that can create it. It is written into that
	/// document instead, and reported in the build, because the project names it; a file the project does
	/// not name is still reported as outside it.
	/// <para>
	/// Listing is turned on here rather than with a legacy project, which would not load the same way
	/// everywhere the suite runs. What decides the answer is the same: no default globs, and a
	/// <c>Compile</c> item naming the file.
	/// </para>
	/// </summary>
	[Test]
	public async Task Creates_a_file_its_project_already_lists()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		var project = fixture.Path("Members", "Library", "Library.csproj");

		string Listing(string named) =>
			$"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <Nullable>enable</Nullable>
			    <ImplicitUsings>enable</ImplicitUsings>
			    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
			  </PropertyGroup>
			  <ItemGroup>
			    <Compile Include="**\*.cs" Exclude="obj\**;bin\**;Listed.cs;Unlisted.cs" />
			    {named}
			  </ItemGroup>
			</Project>
			""";

		var cancellation = TestContext.Current!.Execution.CancellationToken;

		await File.WriteAllTextAsync(project, Listing(string.Empty), cancellation);

		await using var session = await TestSession.OpenAsync(fixture);

		// Named after the session has loaded, which is the order of work the refusal came from: the
		// next call reloads the project and finds a document for a file that is not there.
		await File.WriteAllTextAsync(project, Listing("""<Compile Include="Listed.cs" />"""), cancellation);

		var listedPath = fixture.Path("Members", "Library", "Listed.cs");
		var listed = await AddAsync(session, listedPath, "public static class Listed\n{\n    public static int One => 1;\n}");

		listed.Applied.ShouldBeTrue();
		listed.InTheBuild.ShouldBeTrue("the project names the file");
		listed.IntroducedDiagnostics.ShouldBeEmpty();
		listed.Notices.ShouldNotContain(notice => notice.Contains("lists the files it compiles", StringComparison.Ordinal));
		File.Exists(listedPath).ShouldBeTrue();

		var unlisted = await AddAsync(
			session, fixture.Path("Members", "Library", "Unlisted.cs"), "public static class Unlisted\n{\n}");

		unlisted.InTheBuild.ShouldBeFalse("the project does not name the file");
		unlisted.Notices.ShouldContain(notice => notice.Contains("lists the files it compiles", StringComparison.Ordinal));
	}

	private static Task<AddFileResult> AddAsync(
		WorkspaceSession session,
		string filePath,
		string code,
		bool apply = true,
		IReadOnlyList<string>? usings = null)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new AddFileRequest { FilePath = filePath, Code = code, Apply = apply, Usings = usings ?? [] };

		return session.MutateAsync(
			(snapshot, token) => AddFileService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	/// <summary>
	/// A file added to a project that already has errors in it does not report the project clean. The
	/// rule is the total, not this tool's own contribution: a caller told their project compiles at
	/// the moment it does not will go looking for the error somewhere else entirely.
	/// </summary>
	[Test]
	public async Task Does_not_call_a_project_clean_when_errors_were_already_in_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// One error to be pre-existing by the time the file is added, in the project the file lands in.
		var broken = await MemberEdits.EditAsync(
			session,
			MemberEdits.Request(MemberEditKind.Add, "Library.Prose", "public static string Missing() => Absent.Name;"));

		broken.IntroducedDiagnostics.ShouldNotBeEmpty();

		var result = await AddAsync(
			session,
			fixture.Path("Members", "Library", "Added.cs"),
			"namespace Library;\n\npublic static class Added\n{\n\tpublic static string Name() => \"added\";\n}\n");

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var said = string.Join(" ", result.Notices);

		said.ShouldNotContain("compiles clean", Case.Sensitive);
		said.ShouldContain("were there before this edit", Case.Sensitive);
	}
}
