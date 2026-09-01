using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// Running the fixes a solution's own analyzers ship with. The fixture references nothing special:
/// these are the .NET analyzers every SDK project already has, which is the point -- 206 fix
/// providers over 186 diagnostic ids were measured in this repository with no package added.
/// </summary>
public sealed class CodeFixTests
{
	/// <summary>
	/// CA1822, "mark members as static", chosen because the SDK enables it by default and its fix
	/// changes the declaration in a way that is unmistakable in the text.
	/// </summary>
	private const string Fixable = """
		namespace Core;

		public sealed class Fixable
		{
			public int Value() => 42;

			public int Other() => 7;
		}
		""";

	[Fact]
	public async Task Applies_a_fix_the_projects_own_analyzers_ship()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ApplyAsync(session, "CA1822", path);

		Assert.True(result.Occurrences >= 1, $"CA1822 was not reported: {string.Join(" ", result.Notices)}");
		Assert.True(result.Applied, string.Join(" ", result.Notices));
		Assert.NotEmpty(result.FixTitle);

		var fixedText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		Assert.Contains("static int Value()", fixedText, StringComparison.Ordinal);
	}

	/// <summary>Document scope really is one document: the other file keeps its diagnostic.</summary>
	[Fact]
	public async Task Fixes_every_occurrence_in_the_scope_asked_for()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ApplyAsync(session, "CA1822", path);
		var fixedText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

		// Both members in the file, not just the one the fix started from.
		Assert.Contains("static int Value()", fixedText, StringComparison.Ordinal);
		Assert.Contains("static int Other()", fixedText, StringComparison.Ordinal);
		Assert.Equal([path], result.ChangedFiles);
	}

	[Fact]
	public async Task Previews_without_writing()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ApplyAsync(session, "CA1822", path, apply: false);

		Assert.False(result.Applied);
		Assert.NotEmpty(result.Diff);
		Assert.Equal(Fixable, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Says_so_rather_than_failing_when_nothing_can_fix_the_id()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ApplyAsync(session, "CS0168", path);

		Assert.False(result.Applied);
		Assert.Equal(0, result.Occurrences);
		Assert.NotEmpty(result.Notices);
	}

	[Fact]
	public async Task Lists_what_is_fixable_in_a_file()
	{
		using var fixture = Prepare(out var path);
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var list = await CodeFixService.ListAsync(snapshot, Catalog(), path, TestContext.Current.CancellationToken);

		var fix = list.Fixes.FirstOrDefault(candidate => candidate.DiagnosticId == "CA1822");

		Assert.NotNull(fix);
		Assert.NotEmpty(fix.FixTitles);
		Assert.True(fix.SupportsFixAll);
		Assert.True(fix.Line > 0);
	}

	/// <summary>
	/// The same invariant <see cref="AnalyzerLockTests"/> guards, from the new direction. Fixers are
	/// found by reflecting over the analyzer assembly, and AnalyzerReference.FullPath is deliberately
	/// still the original file -- so loading from it would hold the user's own analyzer open for the
	/// life of the worker, which is exactly what shadow copying exists to prevent.
	/// </summary>
	[Fact]
	public async Task Finds_fixers_without_locking_the_analyzer_it_read_them_from()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		var generatorAssembly = fixture.Path("WithGenerator", "Gen", "bin", "Debug", "netstandard2.0", "Gen.dll");
		var loader = new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance);

		await using var session = await TestSession.OpenAsync(fixture, analyzerLoader: loader);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var catalog = new CodeFixCatalog(loader, NullLogger<CodeFixCatalog>.Instance);
		var project = snapshot.Solution.Projects.First(candidate => candidate.Name == "Consumer");

		// Reads every analyzer assembly the project has, the generator's included.
		Assert.NotEmpty(catalog.FixableIds(project));

		using var stream = new FileStream(generatorAssembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
	}

	private static FixtureSolution Prepare(out string fixablePath)
	{
		var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		fixablePath = fixture.Path("Simple", "Core", "Fixable.cs");
		File.WriteAllText(fixablePath, Fixable);

		return fixture;
	}

	private static CodeFixCatalog Catalog() => new(
		new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
		NullLogger<CodeFixCatalog>.Instance);

	private static Task<Contracts.CodeFixResult> ApplyAsync(
		WorkspaceSession session,
		string diagnosticId,
		string filePath,
		string scope = "document",
		bool apply = true)
	{
		var request = new CodeFixRequest
		{
			DiagnosticId = diagnosticId,
			FilePath = filePath,
			Scope = scope,
			Apply = apply,
		};

		return session.MutateAsync(
			(snapshot, token) => CodeFixService.ApplyAsync(
				snapshot, Catalog(), request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);
	}
}
