namespace RoseMcp.IntegrationTests;

public sealed class RenameTests
{
	[Test]
	public async Task Renames_across_projects_and_writes_the_files()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await RenameAsync(session, fixture, "Product");

		Assert.True(result.Applied);
		Assert.Equal(2, result.FilesChanged);
		Assert.Equal("Multiply", result.OldName);
		Assert.Empty(result.Conflicts);

		var calculator = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);
		var program = await File.ReadAllTextAsync(
			fixture.Path("Simple", "App", "Program.cs"), TestContext.Current!.Execution.CancellationToken);

		Assert.Contains("Product", calculator, StringComparison.Ordinal);
		Assert.DoesNotContain("Multiply", calculator, StringComparison.Ordinal);
		Assert.Contains("Calculator.Product", program, StringComparison.Ordinal);
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

		Assert.Contains("-\tpublic static int Multiply(int left, int right) => left * right;", result.Diff, StringComparison.Ordinal);
		Assert.Contains("+\tpublic static int Product(int left, int right) => left * right;", result.Diff, StringComparison.Ordinal);
		Assert.Contains("@@", result.Diff, StringComparison.Ordinal);
	}

	[Test]
	public async Task Preview_reports_the_diff_without_touching_disk()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);

		var result = await RenameAsync(session, fixture, "Product", apply: false);

		Assert.False(result.Applied, "a preview writes nothing");
		Assert.NotEmpty(result.Diff);

		var after = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);

		Assert.Equal(before, after);

		// A preview must not advance the revision, or the snapshot would disagree with disk.
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		Assert.Equal(result.Revision, snapshot.Revision);
	}

	[Test]
	public async Task Advances_the_revision_when_it_applies()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = (await session.ReadAsync(TestContext.Current!.Execution.CancellationToken)).Revision;
		await RenameAsync(session, fixture, "Product");
		var after = (await session.ReadAsync(TestContext.Current!.Execution.CancellationToken)).Revision;

		Assert.True(after > before);
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

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => RenameAsync(session, fixture, "Product", expectedRevision: 9999));

		Assert.Contains("9999", error.Message, StringComparison.Ordinal);

		var calculator = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);
		Assert.Contains("Multiply", calculator, StringComparison.Ordinal);
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

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => session.MutateAsync(
				(snapshot, token) => RenameService.RenameAsync(snapshot, request, null, token),
				TestContext.Current!.Execution.CancellationToken));

		Assert.Contains("metadata", error.Message, StringComparison.Ordinal);
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

		Assert.Equal("Multiply", result.OldName);
		Assert.True(result.Applied);

		var program = await File.ReadAllTextAsync(
			fixture.Path("Simple", "App", "Program.cs"), TestContext.Current!.Execution.CancellationToken);

		Assert.Contains("Times", program, StringComparison.Ordinal);
		Assert.DoesNotContain("Multiply", program, StringComparison.Ordinal);
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

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => session.MutateAsync(
				(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token),
				TestContext.Current!.Execution.CancellationToken));

		Assert.Contains("matches 2 declarations", thrown.Message, StringComparison.Ordinal);
		Assert.Contains("Name the parameter types", thrown.Message, StringComparison.Ordinal);
	}
}
