namespace RoseMcp.IntegrationTests;

/// <summary>
/// Formatting end to end, through a real design-time build so the .editorconfig is discovered the
/// way it is in a real repository -- walking up from the project, which is where the file that
/// decides tabs and CRLF usually lives.
/// </summary>
public sealed class FormatServiceTests
{
	private const string Lf = "\n";
	private const string Crlf = "\r\n";

	/// <summary>Exactly the rules this repository sets, since they are the ones being honoured.</summary>
	private const string EditorConfig = """
		root = true

		[*.cs]
		indent_style = tab
		indent_size = 4
		end_of_line = crlf
		insert_final_newline = true
		trim_trailing_whitespace = true
		csharp_new_line_before_open_brace = all
		""";

	/// <summary>
	/// Four-space indented, LF-terminated, brace on the same line, a trailing space, and no final
	/// newline: the shape hand-written C# arrives in.
	/// </summary>
	private static readonly string Mangled = string.Join(
		Lf,
		"namespace Core;",
		string.Empty,
		"public sealed class Mangled {",
		"    public int Value { get; set; }   ",
		"    public int Twice() {",
		"        return Value * 2;",
		"    }",
		"}");

	/// <summary>
	/// Correct in every way except its terminators: tabs, Allman braces, no trailing space, a final
	/// newline -- and LF, in a repository whose .editorconfig says CRLF.
	/// </summary>
	private static readonly string OnlyEndings = string.Join(
		Lf,
		"namespace Core;",
		string.Empty,
		"public sealed class Endings",
		"{",
		"\tpublic int Value { get; set; }",
		"}",
		string.Empty);

	/// <summary>
	/// Correct everywhere the formatter can reach and LF inside a verbatim literal, which is what an
	/// edit made by hand into a CRLF file leaves behind. The whitespace pass will not touch the
	/// literal, so this file formats to no change at all and still fails dotnet format.
	/// </summary>
	private static readonly string WithLfInsideALiteral = string.Join(
		Crlf,
		"namespace Core;",
		string.Empty,
		"public static class Literal",
		"{",
		"\tpublic const string Text = @\"one" + Lf + "two\";",
		"}",
		string.Empty);

	[Fact]
	public async Task Formats_a_file_to_what_the_editorconfig_asks_for()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await FormatAsync(session, [path]);

		Assert.True(result.Applied);
		Assert.Equal([path], result.ChangedFiles);

		var formatted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		// Tabs, Allman braces: the formatter's half.
		Assert.Contains("\tpublic int Twice()" + Crlf + "\t{", formatted, StringComparison.Ordinal);
		Assert.DoesNotContain("    public", formatted, StringComparison.Ordinal);

		// Every line ending, the trailing space and the final newline: the half the formatter leaves.
		Assert.DoesNotContain(Lf, formatted.Replace(Crlf, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.DoesNotContain(" " + Crlf, formatted, StringComparison.Ordinal);
		Assert.EndsWith(Crlf, formatted, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Reports_the_diff_without_writing_when_previewing()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await FormatAsync(session, [path], apply: false);

		Assert.False(result.Applied);
		Assert.NotEmpty(result.Diff);
		Assert.Contains("Preview only", string.Join(" ", result.Notices), StringComparison.Ordinal);

		// Which is what makes this usable as a formatting check: the file is untouched.
		Assert.Equal(Mangled, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Says_so_rather_than_failing_when_a_file_is_not_in_the_solution()
	{
		using var fixture = Prepare(out _);
		await using var session = await TestSession.OpenAsync(fixture);

		var absent = fixture.Path("Simple", "Core", "NotHere.cs");
		var result = await FormatAsync(session, [absent]);

		Assert.Empty(result.ChangedFiles);
		Assert.Contains("NotHere.cs", string.Join(" ", result.Notices), StringComparison.Ordinal);
	}

	/// <summary>A file already correct must produce no diff at all, or every call is a false change.</summary>
	[Fact]
	public async Task Changes_nothing_on_a_file_that_is_already_formatted()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		await FormatAsync(session, [path]);
		var second = await FormatAsync(session, [path]);

		Assert.Empty(second.ChangedFiles);
		Assert.Contains("already formatted", string.Join(" ", second.Notices), StringComparison.Ordinal);
	}

	/// <summary>
	/// The change this tool is called for most often is the one a diff cannot render.
	/// <para>
	/// A unified diff compares the content of lines, and a terminator is not content -- so rewriting
	/// every LF in a file to CRLF produces no hunk at all. Left unsaid, a successful call reports
	/// changed files beside an empty diff, which is precisely what a call that did nothing looks
	/// like. The empty diff is asserted here rather than worked around, because it is a property of
	/// what a diff is and not a defect to be fixed in the renderer.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Says_it_rewrote_line_endings_where_the_diff_cannot_show_it()
	{
		using var fixture = Prepare(out _);

		var path = fixture.Path("Simple", "Core", "Endings.cs");
		await File.WriteAllTextAsync(path, OnlyEndings, TestContext.Current.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await FormatAsync(session, [path]);

		Assert.True(result.Applied);
		Assert.Equal([path], result.ChangedFiles);
		Assert.Empty(result.Diff);

		var notices = string.Join(" ", result.Notices);

		Assert.Contains("line ending(s) to CRLF", notices, StringComparison.Ordinal);
		Assert.Contains("Endings.cs", notices, StringComparison.Ordinal);

		var formatted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(Lf, formatted.Replace(Crlf, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
	}

	/// <summary>
	/// #35, the failure mode this project exists to prevent turned on itself: a file whose only
	/// remaining problem is line endings inside a raw literal formats clean, reports success, and
	/// then fails <c>dotnet format</c> at the build. Rewriting it is not the answer -- a newline
	/// inside a literal is part of the string's value -- so the fix is that the tool says so.
	/// </summary>
	[Fact]
	public async Task Says_which_literal_holds_endings_it_would_not_rewrite()
	{
		using var fixture = Prepare(out _);
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Simple", "Core", "Literal.cs");
		await File.WriteAllTextAsync(path, WithLfInsideALiteral, TestContext.Current.CancellationToken);

		var result = await FormatAsync(session, [path]);
		var notices = string.Join(" ", result.Notices);

		Assert.Contains("Literal.cs", notices, StringComparison.Ordinal);
		Assert.Contains("line endings the file does not use", notices, StringComparison.Ordinal);

		// And it is still not rewritten, because doing so would change what the program says.
		var after = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		Assert.Contains("one" + Lf + "two", after, StringComparison.Ordinal);
	}

	/// <summary>
	/// The other half: a literal written with the file's own endings must not be warned about, or
	/// the notice fires on every file holding a multi-line string and stops being read.
	/// </summary>
	[Fact]
	public async Task Says_nothing_about_a_literal_that_already_uses_the_files_endings()
	{
		using var fixture = Prepare(out _);
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Simple", "Core", "Literal.cs");
		await File.WriteAllTextAsync(
			path,
			WithLfInsideALiteral.Replace(Lf, Crlf, StringComparison.Ordinal),
			TestContext.Current.CancellationToken);

		var result = await FormatAsync(session, [path]);

		Assert.DoesNotContain("line endings the file does not use", string.Join(" ", result.Notices), StringComparison.Ordinal);
	}

	private static FixtureSolution Prepare(out string manglePath)
	{
		var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		// At the solution root rather than beside the project, because walking up is how a real
		// repository's .editorconfig is found and the test should exercise that.
		File.WriteAllText(Path.Combine(fixture.Root, "Simple", ".editorconfig"), EditorConfig);

		manglePath = fixture.Path("Simple", "Core", "Mangled.cs");
		File.WriteAllText(manglePath, Mangled);

		return fixture;
	}

	private static Task<Contracts.FormatResult> FormatAsync(
		WorkspaceSession session,
		string[] filePaths,
		bool apply = true)
	{
		var request = new FormatRequest { FilePaths = filePaths, Apply = apply };

		return session.MutateAsync(
			(snapshot, token) => FormatService.FormatAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);
	}
}
