using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A warm worker must not stop you rebuilding your own generator.
/// <para>
/// Loading an assembly holds its file open for the life of the process, and this process is meant
/// to live for hours. Without shadow copying, rebuilding an in-solution generator fails with
/// MSB3021 -- which turns the warm workspace from the feature into the obstacle, because the agent
/// cannot rebuild the very generator it is working on.
/// </para>
/// </summary>
public sealed class AnalyzerLockTests
{
	[Fact]
	public async Task Leaves_the_generator_assembly_writable_while_the_workspace_is_loaded()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		var generatorAssembly = fixture.Path("WithGenerator", "Gen", "bin", "Debug", "netstandard2.0", "Gen.dll");
		Assert.True(File.Exists(generatorAssembly));

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		// Forces the generators to load and run. Nothing is locked until this happens.
		var generated = await GeneratedDocumentService.ListAsync(snapshot, null, TestContext.Current.CancellationToken);
		Assert.Equal(2, generated.Documents.Count);

		// Exactly what MSBuild's Copy task needs to do to refresh bin.
		AssertWritable(generatorAssembly);
	}

	/// <summary>
	/// An analyzer assembly that is on disk and will not load is the silent nothing this server exists
	/// to catch. MSBuild passes the reference to the compiler either way, so the generators inside it
	/// produce nothing while the project reports a clean load and a generator count of zero -- a
	/// workspace that looks healthy and is not. The missing-output check covers the file being absent,
	/// which is a different thing and already reported.
	/// <para>
	/// The assembly is broken by overwriting it rather than by building a generator against a Roslyn
	/// this worker does not have: the failure reaching the loader is the same one, and arranging the
	/// other costs a second fixture that would go stale with every compiler bump.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Reports_why_an_analyzer_failed_to_load()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await File.WriteAllTextAsync(
			fixture.Path("WithGenerator", "Gen", "bin", "Debug", "netstandard2.0", "Gen.dll"),
			"present, named by the project, and not an assembly",
			TestContext.Current.CancellationToken);

		var analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(
			NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance);

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			analyzerLoader,
			NullLogger<SolutionLoader>.Instance);

		var load = await loader.LoadAsync(
			new WorkerOptions { SolutionPath = fixture.SolutionPath },
			TestContext.Current.CancellationToken);

		using var workspace = load.Workspace;

		Assert.Contains(analyzerLoader.LoadFailures, failure => failure.Contains("Gen.dll", StringComparison.Ordinal));

		// The half that matters to a caller: the failure reaches the report rather than stopping at the
		// loader, and takes the workspace out of Loaded with it.
		Assert.Contains(
			load.Report.DegradedReasons,
			reason => reason.Contains("Gen.dll", StringComparison.Ordinal)
				&& reason.Contains("failed to load", StringComparison.Ordinal));

		Assert.Equal(WorkspaceState.Degraded, load.Report.State);
	}

	private static void AssertWritable(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException exception)
		{
			Assert.Fail($"{Path.GetFileName(path)} is locked, so rebuilding it would fail with MSB3021: {exception.Message}");
		}
	}
}
