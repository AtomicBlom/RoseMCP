namespace RoseMcp.Worker.Tests;

/// <summary>
/// Generated sources exist only inside the compilation -- the compiler does not write them to disk
/// unless a project opts in -- so this is the one capability no file-based tool can substitute for.
/// </summary>
public sealed class GeneratedDocumentTests
{
	[Fact]
	public async Task Lists_and_reads_documents_the_generator_produced()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var list = await GeneratedDocumentService.ListAsync(snapshot, null, TestContext.Current.CancellationToken);

		Assert.Equal(
			["GreetableAttribute.g.cs", "Widget.Greeting.g.cs"],
			list.Documents.Select(document => document.HintName).Order());

		// Nothing was written to disk; the only way to see this code is through the compilation.
		Assert.All(list.Documents, document => Assert.False(File.Exists(document.FilePath)));

		var content = await GeneratedDocumentService.ReadAsync(
			snapshot, "Widget.Greeting.g.cs", null, TestContext.Current.CancellationToken);

		Assert.Contains("public string Greet()", content.Text, StringComparison.Ordinal);
		Assert.Contains("from a source generator", content.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Reflects_a_change_to_generator_input_without_a_reload()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);

		var before = await GeneratedDocumentService.ReadAsync(
			await session.ReadAsync(TestContext.Current.CancellationToken),
			"Widget.Greeting.g.cs",
			null,
			TestContext.Current.CancellationToken);

		Assert.Contains("Hello,", before.Text, StringComparison.Ordinal);

		// Change the attribute argument the generator reads, out of band.
		var widget = fixture.Path("WithGenerator", "Consumer", "Widget.cs");
		var source = await File.ReadAllTextAsync(widget, TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			widget, source.Replace("[Greetable(\"Hello\")]", "[Greetable(\"Goodbye\")]"),
			TestContext.Current.CancellationToken);

		var after = await GeneratedDocumentService.ReadAsync(
			await session.ReadAsync(TestContext.Current.CancellationToken),
			"Widget.Greeting.g.cs",
			null,
			TestContext.Current.CancellationToken);

		Assert.Contains("Goodbye,", after.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Explains_itself_when_the_hint_name_is_wrong()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => GeneratedDocumentService.ReadAsync(snapshot, "Nope.g.cs", null, TestContext.Current.CancellationToken));

		// A bare "not found" would leave the caller guessing at the naming convention.
		Assert.Contains("Widget.Greeting.g.cs", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Says_why_the_list_is_empty_when_the_generator_is_not_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var list = await GeneratedDocumentService.ListAsync(snapshot, null, TestContext.Current.CancellationToken);

		Assert.Empty(list.Documents);
		Assert.Contains(list.Notices, notice => notice.Contains("rose_workspace_status", StringComparison.Ordinal));
	}
}
