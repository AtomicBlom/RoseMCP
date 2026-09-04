using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Importing a namespace, which is the step that used to end a semantic write with a text edit.
/// <para>
/// It looks trivial and is not. Sort position, whether System comes first, whether groups are
/// separated, where the file header stays, and the three ways a namespace is already in scope
/// without appearing in the file. Getting the first wrong is IDE0055 and the last is IDE0005, and
/// both are build errors in a repository with the analyzers turned up -- which is the only kind of
/// repository where this matters.
/// </para>
/// </summary>
public sealed class AddUsingTests
{
	/// <summary>
	/// Into the group it belongs to, in order, without disturbing the groups around it.
	/// </summary>
	[Fact]
	public async Task Puts_an_import_where_the_files_own_ordering_puts_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await AddAsync(session, fixture, "Imports.cs", ["System.Text"]);

		Assert.True(result.Applied);
		Assert.Equal(["System.Text"], result.Added);

		var text = await ReadAsync(fixture, "Imports.cs");

		// After System.Globalization, before the blank line that starts the Library group.
		Assert.Contains(
			"using System.Globalization;\r\nusing System.Text;\r\n\r\nusing Library.Nested;\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>A namespace whose group is not there yet starts one, separated the way the file separates them.</summary>
	[Fact]
	public async Task Starts_a_group_when_there_is_none_to_join()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await AddAsync(session, fixture, "Imports.cs", ["Microsoft.Win32"]);

		var text = await ReadAsync(fixture, "Imports.cs");

		Assert.Contains(
			"using System.Globalization;\r\n\r\nusing Library.Nested;\r\n\r\nusing Microsoft.Win32;\r\n",
			text,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// Going in first means inheriting what sat above the old first line. A file header that ends up
	/// under an import is not a formatting quibble -- for an auto-generated marker or a licence it
	/// changes what the file means to other tools.
	/// </summary>
	[Fact]
	public async Task Keeps_the_file_header_above_an_import_added_at_the_top()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await AddAsync(session, fixture, "Imports.cs", ["System.Buffers"]);

		var text = await ReadAsync(fixture, "Imports.cs");

		Assert.StartsWith("// A file header, which has to stay at the top.\r\nusing System.Buffers;\r\n", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// The three ways to be in scope already, none of which shows in this file's import block, and
	/// all of which are IDE0005 if imported again.
	/// </summary>
	[Fact]
	public async Task Refuses_what_is_already_in_scope_and_says_which_way()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Imports.cs");

		var result = await AddAsync(
			session, fixture, "Imports.cs", ["System.Globalization", "System.Collections.Generic", "Library"]);

		Assert.Empty(result.Added);
		Assert.False(result.Applied);
		Assert.Equal(before, await ReadAsync(fixture, "Imports.cs"));

		var reasons = string.Join(" | ", result.AlreadyInScope);

		Assert.Contains("System.Globalization: already imported here", reasons, StringComparison.Ordinal);
		Assert.Contains("System.Collections.Generic: in scope already, from a global or implicit using", reasons, StringComparison.Ordinal);
		Assert.Contains("Library: in scope already, since this file is in namespace Library", reasons, StringComparison.Ordinal);
	}

	/// <summary>
	/// The import earns its keep: the errors it resolves are counted, in the same call that added it.
	/// </summary>
	[Fact]
	public async Task Reports_the_errors_the_import_resolved()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// A member that needs an import the file does not have.
		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Encoded() => Encoding.UTF8.EncodingName;",
		});

		var result = await AddAsync(session, fixture, "Greeter.cs", ["System.Text"]);

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.True(result.ResolvedDiagnosticCount > 0, "the import is what made the file compile");
		Assert.Equal(0, result.TotalErrorCount);
	}

	/// <summary>
	/// The whole point of the argument: the import arrives with the code that needs it, in one call,
	/// so a successful semantic write does not end in a text edit.
	/// </summary>
	[Fact]
	public async Task Imports_in_the_same_call_that_writes_the_code()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Encoded() => Encoding.UTF8.EncodingName;",
			Usings = ["System.Text"],
		});

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Equal(0, result.TotalErrorCount);
		Assert.Contains(result.Notices, notice => notice.Contains("Imported System.Text", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.StartsWith("using System.Text;\r\n\r\nnamespace Library;\r\n", text, StringComparison.Ordinal);
	}

	/// <summary>And one already in scope is reported from that call too, rather than written twice.</summary>
	[Fact]
	public async Task Says_when_the_code_it_wrote_needed_no_import()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public int Counted() => new List<int>().Count;",
			Usings = ["System.Collections.Generic"],
		});

		Assert.True(result.Applied);
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("Did not import System.Collections.Generic", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.DoesNotContain("using System.Collections.Generic;", text, StringComparison.Ordinal);
	}

	private static Task<UsingResult> AddAsync(
		WorkspaceSession session,
		FixtureSolution fixture,
		string file,
		string[] namespaces)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new AddUsingRequest
		{
			FilePath = fixture.Path("Members", "Library", file),
			Namespaces = namespaces,
		};

		return session.MutateAsync(
			(snapshot, token) => AddUsingService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);
	}

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
