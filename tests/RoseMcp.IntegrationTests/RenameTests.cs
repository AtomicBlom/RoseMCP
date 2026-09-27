using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

public sealed class RenameTests
{
	[Test]
	public async Task Renames_across_projects_and_writes_the_files()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await RenameAsync(session, fixture, "Product");

		result.Applied.ShouldBeTrue();
		result.FilesChanged.ShouldBe(2);
		result.OldName.ShouldBe("Multiply");
		result.Conflicts.ShouldBeEmpty();

		var calculator = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);
		var program = await File.ReadAllTextAsync(
			fixture.Path("Simple", "App", "Program.cs"), TestContext.Current!.Execution.CancellationToken);

		calculator.ShouldContain("Product", Case.Sensitive);
		calculator.ShouldNotContain("Multiply", Case.Sensitive);
		program.ShouldContain("Calculator.Product", Case.Sensitive);
	}

	/// <summary>
	/// A refactoring that reports only a file count asks to be trusted. The diff is how a caller
	/// checks, so it has to be real rather than a placeholder.
	/// </summary>
	[Test]
	public async Task Returns_a_unified_diff_of_what_changed()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await RenameAsync(session, fixture, "Product");

		result.Diff.ShouldContain("-\tpublic static int Multiply(int left, int right) => left * right;", Case.Sensitive);
		result.Diff.ShouldContain("+\tpublic static int Product(int left, int right) => left * right;", Case.Sensitive);
		result.Diff.ShouldContain("@@", Case.Sensitive);
	}

	[Test]
	public async Task Preview_reports_the_diff_without_touching_disk()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);

		var result = await RenameAsync(session, fixture, "Product", apply: false);

		result.Applied.ShouldBeFalse("a preview writes nothing");
		result.Diff.ShouldNotBeEmpty();

		var after = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);

		after.ShouldBe(before);

		// A preview must not advance the revision, or the snapshot would disagree with disk.
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		snapshot.Revision.ShouldBe(result.Revision);
	}

	[Test]
	public async Task Advances_the_revision_when_it_applies()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = (await session.ReadAsync(TestContext.Current!.Execution.CancellationToken)).Revision;
		await RenameAsync(session, fixture, "Product");
		var after = (await session.ReadAsync(TestContext.Current!.Execution.CancellationToken)).Revision;

		after.ShouldBeGreaterThan(before);
	}

	/// <summary>
	/// The optimistic-concurrency guard that keeps two clients sharing one broker from silently
	/// overwriting each other in http mode.
	/// </summary>
	[Test]
	public async Task Refuses_to_apply_against_a_stale_revision()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<InvalidOperationException>(
			() => RenameAsync(session, fixture, "Product", expectedRevision: 9999)).OfExactType();

		error.Message.ShouldContain("9999", Case.Sensitive);

		var calculator = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);
		calculator.ShouldContain("Multiply", Case.Sensitive);
	}

	[Test]
	public async Task Refuses_to_rename_a_symbol_that_comes_from_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		// Console in Program.cs resolves to System.Console, which lives in a reference assembly.
		var request = new RenameRequest
		{
			Target = new SymbolTarget { FilePath = fixture.Path("Simple", "App", "Program.cs"), Line = 3, Column = 1 },
			NewName = "Terminal",
		};

		var error = await Should.ThrowAsync<InvalidOperationException>(
			() => session.MutateAsync(
				(snapshot, token) => RenameService.RenameAsync(snapshot, request, null, token),
				TestContext.Current!.Execution.CancellationToken)).OfExactType();

		error.Message.ShouldContain("metadata", Case.Sensitive);
	}

	private static Task<Contracts.RenameResult> RenameAsync(
		WorkspaceSession session,
		FixtureSolution fixture,
		string newName,
		bool apply = true,
		long? expectedRevision = null)
	{
		var request = new RenameRequest
		{
			Target = new SymbolTarget { FilePath = fixture.Path("Simple", "Core", "Calculator.cs"), Line = 7, Column = 20 },
			NewName = newName,
			Apply = apply,
			ExpectedRevision = expectedRevision,
		};

		return session.MutateAsync(
			(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	/// <summary>
	/// A rename by name. Renames arrive in batches more than any other edit, and a position found by
	/// reading the file is wrong the moment an earlier rename in the same batch lands.
	/// </summary>
	[Test]
	public async Task Renames_a_symbol_named_rather_than_pointed_at()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var request = new RenameRequest
		{
			Target = new SymbolTarget { Symbol = "Core.Calculator.Multiply" },
			NewName = "Times",
		};

		var result = await session.MutateAsync(
			(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);

		result.OldName.ShouldBe("Multiply");
		result.Applied.ShouldBeTrue();

		var program = await File.ReadAllTextAsync(
			fixture.Path("Simple", "App", "Program.cs"), TestContext.Current!.Execution.CancellationToken);

		program.ShouldContain("Times", Case.Sensitive);
		program.ShouldNotContain("Multiply", Case.Sensitive);
	}

	/// <summary>
	/// Two overloads are two symbols, and renaming the wrong one is a change that compiles. The name
	/// is refused with both listed rather than resolved to the first.
	/// </summary>
	[Test]
	public async Task Refuses_a_rename_of_an_ambiguous_name()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var request = new RenameRequest
		{
			Target = new SymbolTarget { Symbol = "Library.Greeter.Greet" },
			NewName = "Hail",
		};

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => session.MutateAsync(
				(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token),
				TestContext.Current!.Execution.CancellationToken)).OfExactType();

		thrown.Message.ShouldContain("matches 2 declarations", Case.Sensitive);
		thrown.Message.ShouldContain("Name the parameter types", Case.Sensitive);
	}
}
