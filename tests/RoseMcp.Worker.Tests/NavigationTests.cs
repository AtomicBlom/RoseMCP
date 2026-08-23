namespace RoseMcp.Worker.Tests;

public sealed class NavigationTests
{
	[Fact]
	public async Task Describes_a_symbol_from_its_declaration()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot, fixture.Path("Simple", "Core", "Calculator.cs"), 7, 20, TestContext.Current.CancellationToken);

		Assert.Equal("Multiply", info.Name);
		Assert.Equal("Method", info.Kind);
		Assert.Equal("Public", info.Accessibility);
		Assert.Contains("Core.Calculator.Multiply", info.Signature, StringComparison.Ordinal);
		Assert.True(info.IsFromSource);
		Assert.Single(info.Declarations);
	}

	/// <summary>Pointing at a use site must work as well as pointing at the declaration.</summary>
	[Fact]
	public async Task Describes_a_symbol_from_a_use_site_in_another_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot, fixture.Path("Simple", "App", "Program.cs"), 4, 30, TestContext.Current.CancellationToken);

		Assert.Equal("Multiply", info.Name);
		Assert.Equal(
			fixture.Path("Simple", "Core", "Calculator.cs"),
			info.Declarations.Single().FilePath,
			ignoreCase: true);
	}

	[Fact]
	public async Task Finds_references_across_project_boundaries()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot, fixture.Path("Simple", "Core", "Calculator.cs"), 7, 20, 200, TestContext.Current.CancellationToken);

		var reference = Assert.Single(references.References);

		Assert.Equal(fixture.Path("Simple", "App", "Program.cs"), reference.FilePath, ignoreCase: true);
		Assert.Equal(4, reference.Line);
		Assert.Contains("Calculator.Multiply", reference.Preview!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Explains_a_position_that_is_not_a_symbol()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => NavigationService.DescribeAsync(
				snapshot, fixture.Path("Simple", "Core", "Calculator.cs"), 9999, 1, TestContext.Current.CancellationToken));

		// Guessing at a line number should not produce an opaque index error.
		Assert.Contains("line(s)", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Searches_by_abbreviation()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var result = await NavigationService.SearchAsync(snapshot, "Calc", 50, TestContext.Current.CancellationToken);

		Assert.Contains(result.Matches, match => match.Name == "Calculator" && match.Kind == "NamedType");
	}
}
