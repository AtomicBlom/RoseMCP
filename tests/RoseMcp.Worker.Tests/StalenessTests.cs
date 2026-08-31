using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// The central regression. An agent edits files with its own tools and never tells the workspace,
/// so a read that trusts the last snapshot answers about code that no longer exists. There is no
/// file watcher in play here at all: these tests prove the read barrier's stat sweep is sufficient
/// on its own, which is what makes a dropped watcher event a latency problem rather than a
/// correctness one.
/// </summary>
public sealed class StalenessTests
{
	[Fact]
	public async Task Sees_an_out_of_band_edit_on_the_very_next_read()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var before = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
		Assert.Empty(await ErrorsAsync(before));

		// Edit behind the workspace's back, exactly as an external editor would.
		var calculator = scope.Fixture.Path("Simple", "Core", "Calculator.cs");
		await File.WriteAllTextAsync(
			calculator,
			"namespace Core;" + Environment.NewLine + "public static class Calculator { this is not C# }",
			TestContext.Current.CancellationToken);

		var after = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.NotEmpty(await ErrorsAsync(after));
		Assert.True(after.Revision > before.Revision, "absorbing an external edit must advance the revision");
	}

	[Fact]
	public async Task Reports_no_change_when_nothing_moved()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var first = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
		var second = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		// A sweep that finds nothing must not churn the revision, or callers can never tell
		// whether two answers describe the same world.
		Assert.Equal(first.Revision, second.Revision);
		Assert.Same(first.Solution, second.Solution);
	}

	[Fact]
	public async Task Drops_a_document_whose_file_was_deleted()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var before = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
		Assert.Contains(before.Solution.Projects.SelectMany(project => project.Documents),
			document => document.Name == "Calculator.cs");

		File.Delete(scope.Fixture.Path("Simple", "Core", "Calculator.cs"));

		var after = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.DoesNotContain(after.Solution.Projects.SelectMany(project => project.Documents),
			document => document.Name == "Calculator.cs");
	}

	/// <summary>
	/// Editing a project file cannot be patched into an existing snapshot -- the reference graph,
	/// the document set and the analyzer list all move at once -- so the barrier has to reload
	/// rather than carry on with a text-level patch.
	/// </summary>
	[Fact]
	public async Task Reloads_the_solution_when_a_project_file_changes()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var before = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		var project = scope.Fixture.Path("Simple", "Core", "Core.csproj");
		var text = await File.ReadAllTextAsync(project, TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			project,
			text.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable>" + Environment.NewLine
				+ "    <DefineConstants>$(DefineConstants);RELOADED</DefineConstants>"),
			TestContext.Current.CancellationToken);

		var after = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.True(after.Revision > before.Revision);
		Assert.Contains(after.Notices, notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase));

		var core = after.Solution.Projects.Single(candidate => candidate.Name == "Core");
		Assert.Contains("RELOADED", core.ParseOptions!.PreprocessorSymbolNames);
	}

	/// <summary>
	/// Reads must be ordered behind mutations, not merely serialised with them. A read issued after
	/// a mutation is queued has to observe that mutation.
	/// </summary>
	[Fact]
	public async Task Orders_a_read_behind_a_mutation_queued_before_it()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var mutation = scope.Session.MutateAsync(
			(snapshot, _) =>
			{
				var document = snapshot.Solution.Projects
					.SelectMany(project => project.Documents)
					.Single(candidate => candidate.Name == "Calculator.cs");

				var updated = snapshot.Solution.WithDocumentText(
					document.Id,
					Microsoft.CodeAnalysis.Text.SourceText.From(
						"namespace Core;" + Environment.NewLine + "public static class Calculator { public static int Add(int a, int b) => a + b; }"));

				return Task.FromResult(new MutationResult<long>(snapshot.Revision, updated));
			},
			TestContext.Current.CancellationToken);

		var read = scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		await Task.WhenAll(mutation, read);

		var snapshotAfter = await read;
		Assert.True(snapshotAfter.Revision > await mutation);

		var calculator = snapshotAfter.Solution.Projects
			.SelectMany(project => project.Documents)
			.Single(document => document.Name == "Calculator.cs");

		var source = (await calculator.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
		Assert.DoesNotContain("Multiply", source, StringComparison.Ordinal);
	}

	private static async Task<IReadOnlyList<Diagnostic>> ErrorsAsync(WorkspaceSnapshot snapshot)
	{
		var errors = new List<Diagnostic>();

		foreach (var project in snapshot.Solution.Projects)
		{
			var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);
			errors.AddRange(compilation!.GetDiagnostics(TestContext.Current.CancellationToken)
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		}

		return errors;
	}

	private static async Task<SessionScope> OpenAsync(string name, string solutionFile)
	{
		var fixture = FixtureSolution.Copy(name, solutionFile);

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			new RoseMcp.Worker.Xaml.XamlStubReports(),
			NullLogger<SolutionLoader>.Instance);

		var options = new WorkerOptions { SolutionPath = fixture.SolutionPath };
		var load = await loader.LoadAsync(options, TestContext.Current.CancellationToken);

		return new SessionScope(
			fixture,
			WorkspaceSession.Create(
				load,
				loader,
				options,
				NullLogger<WorkspaceSession>.Instance,
				NullLogger<SolutionWatcher>.Instance));
	}

	/// <summary>Keeps the session and the temp fixture alive together, and tears both down in order.</summary>
	private sealed class SessionScope(FixtureSolution fixture, WorkspaceSession session) : IAsyncDisposable
	{
		public FixtureSolution Fixture { get; } = fixture;

		public WorkspaceSession Session { get; } = session;

		public async ValueTask DisposeAsync()
		{
			await Session.DisposeAsync();
			Fixture.Dispose();
		}
	}
}
