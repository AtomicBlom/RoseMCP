namespace RoseMcp.Worker.Tests;

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

	/// <summary>Diagnostic: surfaces why an analyzer would not load, instead of a silent zero.</summary>
	[Fact]
	public async Task Reports_why_an_analyzer_failed_to_load()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		var loader = new ShadowCopyAnalyzerAssemblyLoader(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance);

		await using var session = await TestSession.OpenAsync(fixture, analyzerLoader: loader);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		await GeneratedDocumentService.ListAsync(snapshot, null, TestContext.Current.CancellationToken);

		Assert.True(loader.LoadFailures.Count == 0,
			"analyzers failed to load: " + string.Join("; ", loader.LoadFailures));
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
