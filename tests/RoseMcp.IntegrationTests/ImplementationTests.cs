namespace RoseMcp.IntegrationTests;

/// <summary>
/// Walking the hierarchy in both directions. Neither question can be answered by searching text: an
/// implementation need not mention the interface anywhere near the member, and an override's
/// documentation usually lives on the base it is hiding.
/// </summary>
public sealed class ImplementationTests
{
	[Fact]
	public async Task Finds_the_types_that_implement_an_interface()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "IShape");

		var result = await NavigationService.FindImplementationsAsync(
			snapshot, path, line, column, 200, TestContext.Current.CancellationToken);

		Assert.Contains("implementing", result.Relationship, StringComparison.Ordinal);
		Assert.Contains("Circle", result.Matches.Select(match => match.Name));
		Assert.Contains("Square", result.Matches.Select(match => match.Name));

		// And they come back with somewhere to go, not just a name.
		Assert.All(result.Matches, match => Assert.NotNull(match.Location));
	}

	[Fact]
	public async Task Finds_the_members_that_implement_an_interface_member()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "Area();");

		var result = await NavigationService.FindImplementationsAsync(
			snapshot, path, line, column, 200, TestContext.Current.CancellationToken);

		Assert.Equal(2, result.Matches.Count);
		Assert.All(result.Matches, match => Assert.Equal("Area", match.Name));
		Assert.Contains(result.Matches, match => match.Signature.Contains("Circle", StringComparison.Ordinal));
		Assert.Contains(result.Matches, match => match.Signature.Contains("Square", StringComparison.Ordinal));
	}

	/// <summary>
	/// A class asks a different question from an interface, and the answer says which one it answered
	/// -- so a caller who pointed at the wrong thing can tell, rather than reading an empty list as
	/// "nothing implements this".
	/// </summary>
	[Fact]
	public async Task Says_which_question_it_answered_for_a_class()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "Square(double side)");

		var result = await NavigationService.FindImplementationsAsync(
			snapshot, path, line, column, 200, TestContext.Current.CancellationToken);

		Assert.Contains("derived", result.Relationship, StringComparison.Ordinal);
		Assert.Empty(result.Matches);
	}

	[Fact]
	public async Task Reports_what_a_member_implements()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "Area() => Math.PI");

		var info = await NavigationService.DescribeAsync(
			snapshot, path, line, column, TestContext.Current.CancellationToken);

		var implemented = Assert.Single(info.BaseDefinitions);

		Assert.Equal("Area", implemented.Name);
		Assert.Contains("IShape", implemented.Signature, StringComparison.Ordinal);
	}

	/// <summary>
	/// One-based line and column of the first occurrence of <paramref name="needle"/>, computed rather
	/// than hardcoded so editing the fixture cannot silently move what these tests point at.
	/// </summary>
	private static (int Line, int Column) At(string path, string needle)
	{
		var text = File.ReadAllText(path);
		var index = text.IndexOf(needle, StringComparison.Ordinal);

		Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

		var before = text[..index];
		var lastBreak = before.LastIndexOf('\n');

		return (before.Count(character => character == '\n') + 1, index - lastBreak);
	}
}
