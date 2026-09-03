namespace RoseMcp.IntegrationTests;

/// <summary>
/// Whether the thing about to be executed is the thing that was just compiled -- the question a
/// green build does not answer.
/// <para>
/// Three separate bugs in one session came from taking an artefact's existence for its currency,
/// and in two of them the solution compiled perfectly: a test ran yesterday's debug host and
/// reported a failure describing a rename that had already been done. So these are timestamp tests,
/// which is what the answer actually rests on.
/// </para>
/// </summary>
public sealed class BuildFreshnessTests
{
	[Fact]
	public async Task Calls_a_project_stale_when_nothing_has_been_built()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var freshness = BuildFreshness.Of(snapshot.Solution, null, TestContext.Current.CancellationToken);

		// A fresh copy has no bin directory at all, which is the state a clone starts in.
		Assert.NotEmpty(freshness);
		Assert.All(freshness, project => Assert.True(project.Stale));
		Assert.All(freshness, project => Assert.Null(project.OutputWrittenUtc));
		Assert.Contains(freshness, project => project.Verdict.Contains("Nothing has been built", StringComparison.Ordinal));
	}

	/// <summary>
	/// Built, and then a source touched: the output exists, the build was green, and the binary is
	/// no longer the code. This is the case that has no other symptom.
	/// </summary>
	[Fact]
	public async Task Notices_a_source_written_after_the_output()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		fixture.Build("Simple", "Core", "Core.csproj");

		await using var session = await TestSession.OpenAsync(fixture);

		var built = await FreshnessAsync(session, "Core");

		Assert.False(built.Stale);
		Assert.NotNull(built.OutputPath);
		Assert.NotNull(built.OutputWrittenUtc);
		Assert.Equal(0, built.SourcesNewerThanOutput);

		// Touched the way an edit touches it, well after the build.
		var calculator = fixture.Path("Simple", "Core", "Calculator.cs");
		File.SetLastWriteTimeUtc(calculator, DateTime.UtcNow.AddMinutes(1));

		var touched = await FreshnessAsync(session, "Core");

		Assert.True(touched.Stale);
		Assert.Equal(1, touched.SourcesNewerThanOutput);
		Assert.Equal(calculator, touched.NewestSourcePath, ignoreCase: true);
		Assert.Contains("Build before running", touched.Verdict, StringComparison.Ordinal);
	}

	/// <summary>
	/// The project file counts as a source. Changing a csproj changes what the assembly is even
	/// when no code moved, which is exactly the case a source-only comparison would miss.
	/// </summary>
	[Fact]
	public async Task Counts_the_project_file_as_a_source()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		fixture.Build("Simple", "Core", "Core.csproj");

		await using var session = await TestSession.OpenAsync(fixture);

		Assert.False((await FreshnessAsync(session, "Core")).Stale);

		File.SetLastWriteTimeUtc(
			fixture.Path("Simple", "Core", "Core.csproj"), DateTime.UtcNow.AddMinutes(1));

		var touched = await FreshnessAsync(session, "Core");

		Assert.True(touched.Stale);
		Assert.EndsWith("Core.csproj", touched.NewestSourcePath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Naming one project answers about that one and not the rest.</summary>
	[Fact]
	public async Task Answers_about_one_project_when_told_which()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.Single(BuildFreshness.Of(snapshot.Solution, "Core", TestContext.Current.CancellationToken));
		Assert.Empty(BuildFreshness.Of(snapshot.Solution, "Nonexistent", TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// Status says so too, in notices and not in degradedReasons. Degraded means the answers cannot
	/// be trusted, and they are exactly as good with a stale bin directory -- these come from
	/// source. It is also the ordinary state of a solution being edited, so putting it in
	/// degradedReasons would mark almost every workspace on the machine degraded.
	/// </summary>
	[Fact]
	public async Task Says_so_in_status_without_calling_the_workspace_degraded()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var status = await WorkspaceStatusReporter.DescribeAsync(
			snapshot.Solution,
			fixture.SolutionPath,
			[],
			restore: null,
			snapshot.Revision,
			loadSeconds: 0,
			TestContext.Current.CancellationToken);

		Assert.Contains(
			status.Notices,
			notice => notice.Contains("newer than their last build output", StringComparison.Ordinal));

		Assert.Empty(status.DegradedReasons);
		Assert.Equal(Contracts.WorkspaceState.Loaded, status.State);
	}

	private static async Task<Contracts.ProjectFreshness> FreshnessAsync(WorkspaceSession session, string project)
	{
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		return Assert.Single(BuildFreshness.Of(snapshot.Solution, project, TestContext.Current.CancellationToken));
	}
}
