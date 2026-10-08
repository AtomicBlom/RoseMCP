using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Generated sources exist only inside the compilation -- the compiler does not write them to disk
/// unless a project opts in -- so this is the one capability no file-based tool can substitute for.
/// </summary>
public sealed class GeneratedDocumentTests
{
	[Test]
	public async Task Lists_and_reads_documents_the_generator_produced()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var list = await GeneratedDocumentService.ListAsync(snapshot, null, TestContext.Current!.Execution.CancellationToken);

		list.Documents.Select(document => document.HintName).Order().ShouldBe(
			["GreetableAttribute.g.cs", "Widget.Greeting.g.cs"]);

		// Nothing was written to disk; the only way to see this code is through the compilation.
		foreach (var document in list.Documents)
		{
			File.Exists(document.FilePath).ShouldBeFalse(
						"generated code has no file on disk to open");
		}

		var content = await GeneratedDocumentService.ReadAsync(
			snapshot, "Widget.Greeting.g.cs", null, TestContext.Current!.Execution.CancellationToken);

		content.Text.ShouldContain("public string Greet()", Case.Sensitive);
		content.Text.ShouldContain("from a source generator", Case.Sensitive);
	}

	/// <summary>
	/// A project name nothing carries is refused, naming the projects there are. Searching the whole
	/// solution instead reports every project's documents as the one asked about, which a caller who
	/// mistyped the name has no way to tell from the answer they wanted.
	/// </summary>
	[Test]
	public async Task Refuses_a_project_the_solution_does_not_have_rather_than_listing_them_all()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var refusal = await Should.ThrowAsync<ArgumentException>(() => GeneratedDocumentService.ListAsync(
			snapshot, "Consumr", TestContext.Current!.Execution.CancellationToken));

		refusal.Message.ShouldContain("No project in this solution is called 'Consumr'", Case.Sensitive);
		refusal.Message.ShouldContain("Consumer", Case.Sensitive);

		var listed = await GeneratedDocumentService.ListAsync(
			snapshot, "consumer", TestContext.Current!.Execution.CancellationToken);

		listed.Documents.ShouldContain(document => document.HintName == "Widget.Greeting.g.cs");
	}

	[Test]
	public async Task Reflects_a_change_to_generator_input_without_a_reload()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);

		var before = await GeneratedDocumentService.ReadAsync(
			await session.ReadAsync(TestContext.Current!.Execution.CancellationToken),
			"Widget.Greeting.g.cs",
			null,
			TestContext.Current!.Execution.CancellationToken);

		before.Text.ShouldContain("Hello,", Case.Sensitive);

		// Change the attribute argument the generator reads, out of band.
		var widget = fixture.Path("WithGenerator", "Consumer", "Widget.cs");
		var source = await File.ReadAllTextAsync(widget, TestContext.Current!.Execution.CancellationToken);
		await File.WriteAllTextAsync(
			widget, source.Replace("[Greetable(\"Hello\")]", "[Greetable(\"Goodbye\")]"),
			TestContext.Current!.Execution.CancellationToken);

		var after = await GeneratedDocumentService.ReadAsync(
			await session.ReadAsync(TestContext.Current!.Execution.CancellationToken),
			"Widget.Greeting.g.cs",
			null,
			TestContext.Current!.Execution.CancellationToken);

		after.Text.ShouldContain("Goodbye,", Case.Sensitive);
	}

	[Test]
	public async Task Explains_itself_when_the_hint_name_is_wrong()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => GeneratedDocumentService.ReadAsync(snapshot, "Nope.g.cs", null, TestContext.Current!.Execution.CancellationToken)).OfExactType();

		// A bare "not found" would leave the caller guessing at the naming convention.
		error.Message.ShouldContain("Widget.Greeting.g.cs", Case.Sensitive);
	}

	[Test]
	public async Task Says_why_the_list_is_empty_when_the_generator_is_not_built()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");

		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var list = await GeneratedDocumentService.ListAsync(snapshot, null, TestContext.Current!.Execution.CancellationToken);

		list.Documents.ShouldBeEmpty();
		list.Notices.ShouldContain(notice => notice.Contains("rose_workspace_status", StringComparison.Ordinal));
	}
}
