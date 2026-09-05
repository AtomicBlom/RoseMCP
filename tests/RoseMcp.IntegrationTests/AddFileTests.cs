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
	[Fact]
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

		var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		// The file's conventions, not the caller's: tabs where four spaces arrived, CRLF where bare
		// newlines did, and a file-scoped namespace.
		Assert.Contains("namespace Library.Nested;\r\n", text, StringComparison.Ordinal);
		Assert.Contains("\tpublic string Text => \"hi\";\r\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("    ", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A namespace the code declares is kept, because there are real reasons to want one that does
	/// not match the folder -- and said out loud, because IDE0130 is a build error where it is
	/// turned up and the caller may not have meant it.
	/// </summary>
	[Fact]
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
	[Fact]
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

		var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		Assert.Contains("using System.Text;", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// A name in two namespaces is reported rather than resolved. The wrong import compiles and
	/// binds to the wrong type, which is the failure with no symptom at all.
	/// </summary>
	[Fact]
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

		var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		Assert.DoesNotContain("using Library.Left;", text, StringComparison.Ordinal);
		Assert.DoesNotContain("using Library.Right;", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Refuses_a_path_that_already_exists()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var before = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Other;"));

		Assert.Contains("already in the solution", thrown.Message, StringComparison.Ordinal);
		Assert.Equal(before, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// Code that does not parse is refused before anything is placed, so a bad call leaves no file
	/// behind -- the same promise every other write here makes.
	/// </summary>
	[Fact]
	public async Task Refuses_code_that_does_not_parse()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Broken.cs");

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => AddAsync(session, path, "public sealed class Broken { public void M() { "));

		Assert.Contains("does not parse", thrown.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(path));
	}

	/// <summary>
	/// A path outside every project's directory would compile nowhere, and saying so beats writing a
	/// file nothing can see.
	/// </summary>
	[Fact]
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
	[Fact]
	public async Task Previews_without_writing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Preview.cs");

		var result = await AddAsync(session, path, "public sealed class Preview;", apply: false);

		Assert.False(result.Applied);
		Assert.NotEmpty(result.Diff);
		Assert.False(File.Exists(path));
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
			TestContext.Current.CancellationToken);
	}
}
