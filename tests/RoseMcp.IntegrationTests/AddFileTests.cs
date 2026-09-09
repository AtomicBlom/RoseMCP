using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

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

		Assert.True(result.Applied);
		Assert.Equal("Library", result.Project);
		Assert.Equal("Library.Nested", result.Namespace);
		Assert.Equal(["Badge"], result.Types);
		Assert.True(result.InTheBuild);
		Assert.Empty(result.IntroducedDiagnostics);

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		// The file's conventions, not the caller's: tabs where four spaces arrived, CRLF where bare
		// newlines did, and a file-scoped namespace.
		Assert.Contains("namespace Library.Nested;\r\n", text, StringComparison.Ordinal);
		Assert.Contains("\tpublic string Text => \"hi\";\r\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("    ", text, StringComparison.Ordinal);
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

		Assert.True(result.Applied);

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		// The blank line between the two using groups, and the one below the namespace.
		Assert.Contains("using System;\r\n\r\nusing System.Linq;\r\n", text, StringComparison.Ordinal);
		Assert.Contains("namespace Library;\r\n\r\n/// <summary>Kept as written.</summary>", text, StringComparison.Ordinal);

		// The documentation tag as written, not respaced around the equals sign.
		Assert.Contains("<param name=\"values\">", text, StringComparison.Ordinal);

		// The braces the caller wrote, and the blank line after them.
		Assert.Contains("\t\tif (values is null)\r\n\t\t{\r\n\t\t\treturn 0;\r\n\t\t}\r\n\r\n", text, StringComparison.Ordinal);

		// The chain still wrapped, one call to a line.
		Assert.Contains(
			"\t\treturn values\r\n\t\t\t.Where(value => value > 0)\r\n\t\t\t.Select(value => value * 2)\r\n\t\t\t.Count();",
			text,
			StringComparison.Ordinal);

		// And the blank line between the two members.
		Assert.Contains("\t}\r\n\r\n\tpublic static string Describe()", text, StringComparison.Ordinal);
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

		Assert.Equal("Library", result.Namespace);
		Assert.Contains(
			result.Notices,
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

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Contains(result.ImportsAdded, line => line.Contains("System.Text", StringComparison.Ordinal));

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		Assert.Contains("using System.Text;", text, StringComparison.Ordinal);
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

		Assert.Contains(
			result.ImportsAmbiguous,
			line => line.Contains("Palette is in 2 namespaces", StringComparison.Ordinal));

		var text = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		Assert.DoesNotContain("using Library.Left;", text, StringComparison.Ordinal);
		Assert.DoesNotContain("using Library.Right;", text, StringComparison.Ordinal);
	}

	[Test]
	public async Task Refuses_a_path_that_already_exists()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var before = await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken);

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Other;"));

		Assert.Contains("already in the solution", thrown.Message, StringComparison.Ordinal);
		Assert.Equal(before, await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken));
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

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Broken { public void M() { "));

		Assert.Contains("does not parse", thrown.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(path), "a refusal writes nothing");
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

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Loose;"));

		Assert.Contains("not inside any project's directory", thrown.Message, StringComparison.Ordinal);
	}

	/// <summary>A preview writes nothing and still answers the question a preview is asking.</summary>
	[Test]
	public async Task Previews_without_writing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Preview.cs");

		var result = await AddAsync(session, path, "public sealed class Preview;", apply: false);

		Assert.False(result.Applied, "a preview says what it would do without doing it");
		Assert.NotEmpty(result.Diff);
		Assert.False(File.Exists(path), "a preview leaves no file behind");
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

		Assert.True(result.Applied);

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Described.cs"),
			TestContext.Current!.Execution.CancellationToken);

		// Kept, which is the invariant the notice exists to explain rather than to fix.
		Assert.Contains("first\nsecond\n", text, StringComparison.Ordinal);

		Assert.Contains(
			result.Notices,
			notice => notice.Contains("line endings the file does not use", StringComparison.Ordinal)
				&& notice.Contains("dotnet format will still ask for them", StringComparison.Ordinal));
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

		Assert.True(result.Applied);

		Assert.DoesNotContain(
			result.Notices,
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

		Assert.True(result.Applied);

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Ordered.cs"),
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(
			text.IndexOf("using System.Globalization;", StringComparison.Ordinal)
				< text.IndexOf("using Library.Nested;", StringComparison.Ordinal),
			$"System did not come first: {text}");
	}

	private static Task<AddFileResult> AddAsync(
		WorkspaceSession session,
		string filePath,
		string code,
		bool apply = true)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new AddFileRequest { FilePath = filePath, Code = code, Apply = apply };

		return session.MutateAsync(
			(snapshot, token) => AddFileService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}
}
