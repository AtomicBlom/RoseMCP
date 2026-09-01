using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.IntegrationTests;

public sealed class DiagnosticsTests
{
	[Fact]
	public async Task Reports_nothing_for_a_clean_solution()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var service = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var result = await service.AnalyseAsync(
			await session.ReadAsync(TestContext.Current.CancellationToken),
			new DiagnosticsRequest(),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		Assert.False(result.Truncated);
		Assert.False(result.IncludedAnalyzers);
	}

	[Fact]
	public async Task Reports_an_error_introduced_out_of_band_with_its_location()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var service = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var calculator = fixture.Path("Simple", "Core", "Calculator.cs");
		await File.WriteAllTextAsync(
			calculator,
			"namespace Core;" + Environment.NewLine
				+ "public static class Calculator" + Environment.NewLine
				+ "{" + Environment.NewLine
				+ "\tpublic static int Add(int left, int right) => left + nope;" + Environment.NewLine
				+ "}",
			TestContext.Current.CancellationToken);

		var result = await service.AnalyseAsync(
			await session.ReadAsync(TestContext.Current.CancellationToken),
			new DiagnosticsRequest { MinimumSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity.Error },
			TestContext.Current.CancellationToken);

		var error = result.Diagnostics.First(diagnostic => diagnostic.Id == "CS0103");

		Assert.Equal("Error", error.Severity);
		Assert.Equal(calculator, error.FilePath, ignoreCase: true);
		Assert.Equal(4, error.Line);
		Assert.Null(error.GeneratedHintName);
	}

	/// <summary>
	/// The cache is keyed on Roslyn's dependent semantic version. A cache that never hits behaves
	/// identically from the outside, so the counter is the only way to tell.
	/// </summary>
	[Fact]
	public async Task Does_not_recompile_when_nothing_changed()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var service = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		await service.AnalyseAsync(snapshot, new DiagnosticsRequest(), TestContext.Current.CancellationToken);

		var afterFirst = service.CompilationsAnalysed;
		Assert.True(afterFirst > 0, "the first pass must actually analyse something");

		for (var i = 0; i < 3; i++)
		{
			await service.AnalyseAsync(
				await session.ReadAsync(TestContext.Current.CancellationToken),
				new DiagnosticsRequest(),
				TestContext.Current.CancellationToken);
		}

		Assert.Equal(afterFirst, service.CompilationsAnalysed);
	}

	[Fact]
	public async Task Recomputes_after_a_semantic_change()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var service = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		await service.AnalyseAsync(
			await session.ReadAsync(TestContext.Current.CancellationToken),
			new DiagnosticsRequest(),
			TestContext.Current.CancellationToken);

		var afterFirst = service.CompilationsAnalysed;

		await File.WriteAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"),
			"namespace Core;" + Environment.NewLine + "public static class Calculator { public static int Add() => 1; }",
			TestContext.Current.CancellationToken);

		await service.AnalyseAsync(
			await session.ReadAsync(TestContext.Current.CancellationToken),
			new DiagnosticsRequest(),
			TestContext.Current.CancellationToken);

		Assert.True(service.CompilationsAnalysed > afterFirst, "an edit must invalidate the cache");
	}
}
