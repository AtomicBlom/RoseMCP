using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The central regression. An agent edits files with its own tools and never tells the workspace,
/// so a read that trusts the last snapshot answers about code that no longer exists. There is no
/// file watcher in play here at all: these tests prove the read barrier's stat sweep is sufficient
/// on its own, which is what makes a dropped watcher event a latency problem rather than a
/// correctness one.
/// </summary>
public sealed class StalenessTests
{
	[Test]
	public async Task Sees_an_out_of_band_edit_on_the_very_next_read()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var before = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		Assert.Empty(await ErrorsAsync(before));

		// Edit behind the workspace's back, exactly as an external editor would.
		var calculator = scope.Fixture.Path("Simple", "Core", "Calculator.cs");
		await File.WriteAllTextAsync(
			calculator,
			"namespace Core;" + Environment.NewLine + "public static class Calculator { this is not C# }",
			TestContext.Current!.Execution.CancellationToken);

		var after = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		Assert.NotEmpty(await ErrorsAsync(after));
		Assert.True(after.Revision > before.Revision, "absorbing an external edit must advance the revision");
	}

	/// <summary>
	/// The other half of the sweep: a file this worker wrote is already in the snapshot, so reading
	/// it back off disk is work with nothing at the end of it.
	/// <para>
	/// The tracking table kept the stamp from before the write, so the next barrier found every file
	/// the mutation had touched, re-read it, advanced the revision and reported it as somebody
	/// else's edit. Throwing the compilation away is what that costs, since every source generator
	/// then runs again -- and on a project with XAML that is every stub re-emitted, for text nothing
	/// had changed.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_write_of_our_own_is_not_read_back_as_an_external_change()
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

		Assert.True(result.Applied);
		Assert.True(result.ChangedFiles.Count > 1, "the rename has to write more than one file for this to be worth asserting");

		var settled = session.Revision;
		var after = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		Assert.DoesNotContain(after.Notices, notice => notice.Contains("external file change", StringComparison.Ordinal));
		Assert.Equal(settled, after.Revision);
	}

	/// <summary>
	/// Absorbing an edit is the server working, not a reason to distrust it.
	/// <para>
	/// These notices were being appended to degradedReasons, so almost every status call after an
	/// edit reported the workspace degraded -- which empties the word, and is the same false alarm
	/// that narrowed the MSBuild-failure count and took targetFramework out of the project name.
	/// Worse, State was computed before the append, so a report could say Loaded while listing a
	/// degraded reason. Found only because status gained a notices field to sit beside it.
	/// </para>
	/// </summary>
	[Test]
	public async Task Absorbing_an_edit_is_a_notice_and_not_a_reason_to_distrust_anything()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		await using var host = new WorkspaceHost(
			new WorkerOptions { SolutionPath = fixture.SolutionPath },
			loader,
			new SharedWorkProgress(),
			NullLoggerFactory.Instance,
			new NeverStops(),
			NullLogger<WorkspaceHost>.Instance);

		await host.StartAsync(TestContext.Current!.Execution.CancellationToken);
		await host.GetStatusAsync(TestContext.Current!.Execution.CancellationToken);

		// Edited behind the workspace's back, so the next status reconciles and absorbs it.
		await File.WriteAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"),
			"namespace Core;" + Environment.NewLine
				+ "public static class Calculator { public static int Add(int a, int b) => a + b; }",
			TestContext.Current!.Execution.CancellationToken);

		var status = await host.GetStatusAsync(TestContext.Current!.Execution.CancellationToken);

		Assert.Contains(status.Notices, notice => notice.Contains("Absorbed", StringComparison.Ordinal));
		Assert.DoesNotContain(status.DegradedReasons, reason => reason.Contains("Absorbed", StringComparison.Ordinal));

		// And the two fields agree, which they could not before.
		Assert.Empty(status.DegradedReasons);
		Assert.Equal(WorkspaceState.Loaded, status.State);
	}

	[Test]
	public async Task Reports_no_change_when_nothing_moved()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var first = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var second = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		// A sweep that finds nothing must not churn the revision, or callers can never tell
		// whether two answers describe the same world.
		Assert.Equal(first.Revision, second.Revision);
		Assert.Same(first.Solution, second.Solution);
	}

	[Test]
	public async Task Drops_a_document_whose_file_was_deleted()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var before = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		Assert.Contains(before.Solution.Projects.SelectMany(project => project.Documents),
			document => document.Name == "Calculator.cs");

		File.Delete(scope.Fixture.Path("Simple", "Core", "Calculator.cs"));

		var after = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		Assert.DoesNotContain(after.Solution.Projects.SelectMany(project => project.Documents),
			document => document.Name == "Calculator.cs");
	}

	/// <summary>
	/// Editing a project file cannot be patched into an existing snapshot -- the reference graph,
	/// the document set and the analyzer list all move at once -- so the barrier has to reload
	/// rather than carry on with a text-level patch.
	/// </summary>
	[Test]
	public async Task Reloads_the_solution_when_a_project_file_changes()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		var before = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var project = scope.Fixture.Path("Simple", "Core", "Core.csproj");
		var text = await File.ReadAllTextAsync(project, TestContext.Current!.Execution.CancellationToken);
		await File.WriteAllTextAsync(
			project,
			text.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable>" + Environment.NewLine
				+ "    <DefineConstants>$(DefineConstants);RELOADED</DefineConstants>"),
			TestContext.Current!.Execution.CancellationToken);

		var after = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		Assert.True(after.Revision > before.Revision);
		Assert.Contains(after.Notices, notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase));

		var core = after.Solution.Projects.Single(candidate => candidate.Name == "Core");
		Assert.Contains("RELOADED", core.ParseOptions!.PreprocessorSymbolNames);
	}

	/// <summary>
	/// A reload that throws leaves the tracking table describing the snapshot still in hand.
	/// <para>
	/// The sweep works out that a source file changed and that a project file changed, and the second
	/// of those is what sends the barrier round a reload. Recording the sweep's stamps as it goes
	/// would leave every one of them equal to disk against a snapshot that predates both changes, so
	/// the read after the failure finds nothing to do and serves it -- a confident answer about code
	/// that is not there, which is the failure the barrier exists to rule out. The reload is made to
	/// fail by holding the solution file open exclusively, which is the first thing a load reads.
	/// </para>
	/// </summary>
	[Test]
	public async Task Absorbs_the_change_on_the_next_read_when_a_reload_throws()
	{
		await using var scope = await OpenAsync("Simple", "Simple.sln");

		await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		await File.WriteAllTextAsync(
			scope.Fixture.Path("Simple", "Core", "Calculator.cs"),
			"namespace Core;" + Environment.NewLine
				+ "public static class Calculator { public static int Tripled(int a) => a * 3; }",
			TestContext.Current!.Execution.CancellationToken);

		// The project file is what forces a reload. A source edit on its own is patched into the
		// snapshot and never reaches one, so it could not show this.
		var projectFile = scope.Fixture.Path("Simple", "Core", "Core.csproj");
		var projectText = await File.ReadAllTextAsync(projectFile, TestContext.Current!.Execution.CancellationToken);
		await File.WriteAllTextAsync(
			projectFile,
			projectText.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable>" + Environment.NewLine
				+ "    <DefineConstants>$(DefineConstants);RELOADED</DefineConstants>"),
			TestContext.Current!.Execution.CancellationToken);

		using (new FileStream(scope.Fixture.SolutionPath, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			await Assert.ThrowsAnyAsync<IOException>(
				() => scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken));
		}

		var after = await scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var calculator = after.Solution.Projects
			.SelectMany(project => project.Documents)
			.Single(document => document.Name == "Calculator.cs");

		var source = (await calculator.GetTextAsync(TestContext.Current!.Execution.CancellationToken)).ToString();
		Assert.Contains("Tripled", source, StringComparison.Ordinal);

		// And the structural half of the same sweep survived too.
		var core = after.Solution.Projects.Single(candidate => candidate.Name == "Core");
		Assert.Contains("RELOADED", core.ParseOptions!.PreprocessorSymbolNames);
	}

	/// <summary>
	/// Reads must be ordered behind mutations, not merely serialised with them. A read issued after
	/// a mutation is queued has to observe that mutation.
	/// </summary>
	[Test]
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
			TestContext.Current!.Execution.CancellationToken);

		var read = scope.Session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		await Task.WhenAll(mutation, read);

		var snapshotAfter = await read;
		Assert.True(snapshotAfter.Revision > await mutation);

		var calculator = snapshotAfter.Solution.Projects
			.SelectMany(project => project.Documents)
			.Single(document => document.Name == "Calculator.cs");

		var source = (await calculator.GetTextAsync(TestContext.Current!.Execution.CancellationToken)).ToString();
		Assert.DoesNotContain("Multiply", source, StringComparison.Ordinal);
	}

	private static async Task<IReadOnlyList<Diagnostic>> ErrorsAsync(WorkspaceSnapshot snapshot)
	{
		var errors = new List<Diagnostic>();

		foreach (var project in snapshot.Solution.Projects)
		{
			var compilation = await project.GetCompilationAsync(TestContext.Current!.Execution.CancellationToken);
			errors.AddRange(compilation!.GetDiagnostics(TestContext.Current!.Execution.CancellationToken)
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		}

		return errors;
	}

	private static Task<SessionScope> OpenAsync(string name, string solutionFile) =>
		SessionScope.OpenAsync(name, solutionFile);
}
