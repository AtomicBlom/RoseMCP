using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Working out which namespace a name needs, which is the half of importing that the caller cannot
/// always supply.
/// <para>
/// The value is in what it refuses. Answering "System.Text" for Encoding is easy and saves one
/// round trip; answering one of two namespaces for a name that lives in both is the failure this
/// repository is otherwise careful to design out, because the wrong import compiles and binds to
/// the wrong type. So most of what is checked here is the cases with no single answer, and that
/// each of them says which kind of no-answer it is.
/// </para>
/// </summary>
public sealed class ResolveNameTests
{
	/// <summary>The ordinary case: one namespace, named, ready to hand to an import.</summary>
	[Test]
	public async Task Finds_the_one_namespace_that_would_resolve_a_name()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Encoding", InLibrary(fixture, "Greeter.cs"));

		result.Name.ShouldBe("Encoding");
		result.Import.ShouldBe("System.Text");
		result.Candidates.ShouldContain(candidate => candidate.Symbol == "System.Text.Encoding");
	}

	/// <summary>
	/// Two namespaces is two candidates and no answer. Returning the first would compile, which is
	/// exactly why it cannot be done: the caller would never find out it bound to the wrong type.
	/// </summary>
	[Test]
	public async Task Refuses_to_choose_between_two_namespaces()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Palette", InLibrary(fixture, "Greeter.cs"));

		result.Import.ShouldBeNull();
		result.Candidates.Count.ShouldBe(2);
		result.Candidates.Select(candidate => candidate.Namespace).Order(StringComparer.Ordinal).ShouldBe(
			["Library.Left", "Library.Right"]);

		result.Notices.ShouldContain(notice => notice.Contains("Pick one", StringComparison.Ordinal));
	}

	/// <summary>
	/// A name qualified at the use site resolves on its first segment, because that is the part the
	/// compiler failed on -- searching for UTF8 would find nothing, and say so with confidence.
	/// </summary>
	[Test]
	public async Task Resolves_the_first_segment_of_a_qualified_use()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Encoding.UTF8", InLibrary(fixture, "Greeter.cs"));

		result.Name.ShouldBe("Encoding");
		result.Import.ShouldBe("System.Text");
	}

	/// <summary>
	/// Already imported is not an answer, and saying so is the point: it means the error is
	/// something other than a missing import, and adding it again is IDE0005.
	/// </summary>
	[Test]
	public async Task Says_when_the_namespace_is_in_scope_already()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "CultureInfo", InLibrary(fixture, "Imports.cs"));

		result.Import.ShouldBeNull();
		result.Candidates.ShouldContain(candidate => candidate.AlreadyInScope == "already imported here");
		result.Notices.ShouldContain(notice => notice.Contains("in scope already", StringComparison.Ordinal));
	}

	/// <summary>
	/// The implicit usings the SDK adds are in scope without appearing anywhere in the file, which
	/// is exactly what a caller reading the import block would get wrong.
	/// </summary>
	[Test]
	public async Task Says_when_an_implicit_using_already_covers_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "List", InLibrary(fixture, "Greeter.cs"));

		result.Import.ShouldBeNull();
		result.Candidates.ShouldContain(
			candidate => candidate.Namespace == "System.Collections.Generic"
				&& candidate.AlreadyInScope == "in scope already, from a global or implicit using");
	}

	/// <summary>
	/// The third way a name fails: it is not a type at all, and the namespace has to be imported for
	/// a method that does not bear its name.
	/// </summary>
	[Test]
	public async Task Finds_an_extension_method_where_nothing_of_that_name_is_a_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Shouted", InLibrary(fixture, "Greeter.cs"));

		result.Import.ShouldBe("Library.Extras");
		result.Candidates.ShouldContain(candidate => candidate.Kind == "ExtensionMethod");
		result.Notices.ShouldContain(notice => notice.Contains("extension methods", StringComparison.Ordinal));
	}

	/// <summary>
	/// A nested type is findable and still not importable: the namespace holds its container, not
	/// it. Offering it as an import would produce a directive that changes nothing and a second
	/// error reading the same as the first.
	/// </summary>
	[Test]
	public async Task Says_a_nested_type_cannot_be_reached_by_importing_alone()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Inner", InLibrary(fixture, "Greeter.cs"));

		result.Import.ShouldBeNull();

		var candidate = result.Candidates.ShouldHaveSingleItem();

		candidate.Namespace.ShouldBe("Library.Deep");
		candidate.Caveat.ShouldNotBeNull();
		candidate.Caveat.ShouldContain("nested in Outer", Case.Sensitive);
		candidate.Caveat.ShouldContain("write Outer.Inner", Case.Sensitive);
	}

	/// <summary>
	/// A name used with type arguments the type does not take is not an import problem, and the
	/// count is the only thing that says so -- the name itself matches perfectly.
	/// </summary>
	[Test]
	public async Task Says_when_the_arity_does_not_match_how_the_name_was_used()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Outer<string>", InLibrary(fixture, "Greeter.cs"));

		result.Import.ShouldBeNull();

		var candidate = result.Candidates.ShouldHaveSingleItem();

		candidate.Arity.ShouldBe(0);
		candidate.Caveat.ShouldNotBeNull();
		candidate.Caveat.ShouldContain("takes 0 type argument(s), and the name was used with 1", Case.Sensitive);
	}

	/// <summary>Nothing of that name anywhere is a real answer, and a different one from "pick a namespace".</summary>
	[Test]
	public async Task Says_when_nothing_is_called_that_at_all()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Nonexistent", InLibrary(fixture, "Greeter.cs"));

		result.Import.ShouldBeNull();
		result.Candidates.ShouldBeEmpty();
		result.Notices.ShouldContain(notice => notice.Contains("not written yet", StringComparison.Ordinal));
	}

	/// <summary>
	/// Written, but in a project this one cannot see. No import resolves it, and a caller told only
	/// that nothing was found would go looking for a typo in a name that is spelled right.
	/// </summary>
	[Test]
	public async Task Says_when_the_type_is_in_a_project_this_one_does_not_reference()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ResolveAsync(session, "Announcer", fixture.Path("Simple", "Core", "Calculator.cs"));

		result.Import.ShouldBeNull();

		var candidate = result.Candidates.ShouldHaveSingleItem();

		candidate.Caveat.ShouldNotBeNull();
		candidate.Caveat.ShouldContain("which Core does not reference", Case.Sensitive);
		candidate.Caveat.ShouldContain("add the project reference first", Case.Sensitive);
	}

	/// <summary>
	/// The namespace arrives with the error, in the result of the write that caused it, off a
	/// compilation built anyway. With resolveUsings off the import is reported rather than added, which
	/// is the shape a caller wanting to place it themselves asks for.
	/// </summary>
	[Test]
	public async Task Names_the_import_in_the_result_of_the_write_that_needed_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Encoded() => Encoding.UTF8.EncodingName;",
			ResolveUsings = false,
		});

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldContain(entry => entry.Id == "CS0103");
		result.Notices.ShouldContain(
			notice => notice.Contains("System.Text.Encoding", StringComparison.Ordinal)
				&& notice.Contains("usings: [\"System.Text\"]", StringComparison.Ordinal));
	}

	/// <summary>And where there is no single namespace, the write says so rather than naming one.</summary>
	[Test]
	public async Task Reports_the_choice_rather_than_an_import_when_the_name_is_ambiguous()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,
			Symbol = "Library.Greeter",
			Code = "public string Painted() => Palette.Name;",
		});

		result.Notices.ShouldContain(
			notice => notice.Contains("Palette is in 2 namespaces", StringComparison.Ordinal)
				&& notice.Contains("Library.Left", StringComparison.Ordinal));

		result.Notices.ShouldNotContain(notice => notice.Contains("pass usings:", StringComparison.Ordinal));
	}

	/// <summary>
	/// The one common diagnostic an IDE fixes and this cannot. The add-import provider lives in
	/// Microsoft.CodeAnalysis.CSharp.Features, which is not loaded here, so an unresolved name reads
	/// as one more thing nothing can repair unless the answer says otherwise.
	/// </summary>
	[Test]
	public async Task Says_the_add_import_fix_is_not_here_and_what_is()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var path = fixture.Path("Simple", "Core", "Unresolved.cs");

		await File.WriteAllTextAsync(
			path,
			"namespace Core;\r\n\r\npublic static class Unresolved\r\n{\r\n\tpublic static string Name() "
				+ "=> Encoding.UTF8.EncodingName;\r\n}\r\n",
			TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var catalog = new CodeFixCatalog(
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<CodeFixCatalog>.Instance);

		var list = await CodeFixService.ListAsync(snapshot, catalog, path, TestContext.Current!.Execution.CancellationToken);

		// Two fixers claim CS0103 -- generate variable and generate method -- and neither offers
		// anything here, which used to drop it out of both lists and out of the answer altogether.
		list.UnfixableIds.ShouldContain("CS0103");
		list.Fixes.ShouldNotContain(fix => fix.DiagnosticId == "CS0103");
		list.Notices.ShouldContain(notice => notice.Contains("rose_resolve_name", StringComparison.Ordinal));
	}

	private static string InLibrary(FixtureSolution fixture, string file) =>
		fixture.Path("Members", "Library", file);

	private static async Task<NameResolutionResult> ResolveAsync(
		WorkspaceSession session,
		string name,
		string? filePath = null,
		int? arity = null)
	{
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		return await NameResolver.ResolveAsync(
			snapshot,
			new ResolveNameRequest { Name = name, FilePath = filePath, Arity = arity },
			TestContext.Current!.Execution.CancellationToken);
	}

	private static Task<MemberEditResult> EditAsync(WorkspaceSession session, MemberEditRequest request)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		return session.MutateAsync(
			(snapshot, token) => MemberEditService.EditAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}
}
