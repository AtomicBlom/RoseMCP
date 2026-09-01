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
