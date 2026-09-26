using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;
using RoseMcp.Worker.Xaml;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The whole pipeline over a real solution: find the markup, add it as additional documents, run the
/// generator, and have the code-behind bind. The fixture declares its own Windows.UI.Xaml types, so
/// this needs no Windows SDK and no UWP tooling -- only the shape of the problem, not its scale.
/// </summary>
public sealed class XamlWorkspaceTests
{
	[Test]
	public async Task A_xaml_project_compiles_without_its_markup_compiler_ever_running()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var diagnostics = await DiagnoseAsync(session);

		// Without the stub, this project has an unresolved InitializeComponent and an unknown Save.
		diagnostics.Diagnostics.ShouldBeEmpty();
	}

	/// <summary>
	/// The stub has to be a source-generated document, not a file: readable through the generated
	/// document tools, and never written to disk beside the user's code.
	/// </summary>
	[Test]
	public async Task The_stub_is_generated_code_and_stays_out_of_the_tree()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var generated = await GeneratedDocumentService.ListAsync(
			snapshot, null, TestContext.Current!.Execution.CancellationToken);

		var stub = generated.Documents.Where(document =>
			document.HintName.Contains("xamlstub", StringComparison.OrdinalIgnoreCase)).ShouldHaveSingleItem();

		stub.HintName.ShouldContain("Widget", Case.Sensitive);
		File.Exists(fixture.Path("XamlStub", "Ui", "Widget.xamlstub.g.cs")).ShouldBeFalse();

		var content = await GeneratedDocumentService.ReadAsync(
			snapshot, stub.HintName, null, TestContext.Current!.Execution.CancellationToken);

		content.Text.ShouldContain("partial class Widget : global::Windows.UI.Xaml.Controls.UserControl", Case.Sensitive);
		content.Text.ShouldContain("private global::Windows.UI.Xaml.Controls.Button Save;", Case.Sensitive);
	}

	[Test]
	public async Task Reports_which_dialect_it_chose_and_on_what_evidence()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// Generators are lazy; asking for the compilation is what runs them.
		await DiagnoseAsync(session);

		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var project = snapshot.Solution.Projects.Single(candidate => candidate.Name.StartsWith("Ui", StringComparison.Ordinal));
		var report = await XamlStubReportReader.ReadAsync(project, TestContext.Current!.Execution.CancellationToken);

		report.ShouldNotBeNull();
		report.Dialect.ShouldBe("UWP");
		report.DialectAmbiguous.ShouldBeFalse("the dialect was not ambiguous");
		report.DialectReason.ShouldContain("Windows.UI.Xaml.Controls.Control", Case.Sensitive);
		report.MarkupFileCount.ShouldBe(1);
		report.StubbedClassCount.ShouldBe(1);
		report.UnresolvedTypes.ShouldBeEmpty();
	}

	/// <summary>
	/// Markup is tracked like any other file, so editing a .xaml behind the workspace's back changes
	/// the generated partial on the next read -- no reload, no refresh call.
	/// </summary>
	[Test]
	public async Task Picks_up_a_new_named_element_when_the_markup_changes_on_disk()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var markupPath = fixture.Path("XamlStub", "Ui", "Widget.xaml");
		var markup = await File.ReadAllTextAsync(markupPath, TestContext.Current!.Execution.CancellationToken);

		await File.WriteAllTextAsync(
			markupPath,
			markup.Replace(
				"<Button x:Name=\"Save\" Label=\"Save\" />",
				"<Button x:Name=\"Save\" Label=\"Save\" />\r\n\t\t<Button x:Name=\"Cancel\" />",
				StringComparison.Ordinal),
			TestContext.Current!.Execution.CancellationToken);

		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var generated = await GeneratedDocumentService.ListAsync(
			snapshot, null, TestContext.Current!.Execution.CancellationToken);

		var stub = generated.Documents.Where(document =>
			document.HintName.Contains("xamlstub", StringComparison.OrdinalIgnoreCase)).ShouldHaveSingleItem();

		var content = await GeneratedDocumentService.ReadAsync(
			snapshot, stub.HintName, null, TestContext.Current!.Execution.CancellationToken);

		content.Text.ShouldContain("Button Cancel;", Case.Sensitive);
	}

	[Test]
	public async Task Leaves_the_workspace_alone_when_stubs_are_turned_off()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var load = await loader.LoadAsync(
			new WorkerOptions { SolutionPath = fixture.SolutionPath, NoXamlStubs = true },
			TestContext.Current!.Execution.CancellationToken);

		try
		{
			var project = load.Solution.Projects.Single();

			project.AdditionalDocuments.ShouldBeEmpty();
			(await project.GetSourceGeneratedDocumentsAsync(TestContext.Current!.Execution.CancellationToken)).ShouldBeEmpty();
		}
		finally
		{
			load.Workspace.Dispose();
		}
	}

	/// <summary>
	/// Through the host, which is the path the status tool takes -- and which had its own call to the
	/// reporter, so it went on returning zeroes after the loader's call was fixed. Found by asking
	/// the deployed server about a real solution, and worth a test rather than another deploy.
	/// </summary>
	[Test]
	public async Task The_status_tool_reports_what_was_stubbed()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");

		var options = new WorkerOptions { SolutionPath = fixture.SolutionPath };
		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		await using var host = new WorkspaceHost(
			options,
			loader,
			new SharedWorkProgress(),
			NullLoggerFactory.Instance,
			new NeverStops(),
			NullLogger<WorkspaceHost>.Instance);

		await host.StartAsync(TestContext.Current!.Execution.CancellationToken);

		var status = await host.GetStatusAsync(TestContext.Current!.Execution.CancellationToken);
		var project = status.Projects.ShouldHaveSingleItem();

		project.XamlMarkupCount.ShouldBe(1);
		project.XamlStubbedCount.ShouldBe(1);
		project.XamlDialect.ShouldBe("UWP");
		project.UnresolvedXamlTypes.ShouldBeEmpty();

		// Stubbing successfully is not a reason to call the workspace degraded.
		status.DegradedReasons.ShouldBeEmpty();
		status.State.ShouldBe(WorkspaceState.Loaded);
	}

	/// <summary>
	/// A member-level find-references in a project that has stub generation attached.
	/// <para>
	/// This is the shape that a custom AnalyzerReference broke, and it broke silently in tests
	/// because nothing here asked for it. Roslyn walks up from a member to the interface members it
	/// implements, which needs the per-project index behind FindDerivedClasses, which checksums
	/// every analyzer reference the project has -- and its serializer throws on any reference type
	/// it does not recognise. Type-level searches never build that index, so they went on working
	/// and hid it. Greeter exists in the fixture only to force the walk: an interface to go up to,
	/// and an unsealed class so going down again is not skipped.
	/// </para>
	/// </summary>
	[Test]
	public async Task Finds_references_to_a_member_of_a_project_carrying_stub_generation()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { FilePath = fixture.Path("XamlStub", "Ui", "Greeter.cs"), Line = 16, Column = 24 },
			200,
			TestContext.Current!.Execution.CancellationToken);

		references.Symbol.ShouldContain("Greeter.Greet", Case.Sensitive);
		references.References.ShouldContain(reference => reference.Line == 21);
	}

	/// <summary>The other tool that reaches the same index, by the same route.</summary>
	[Test]
	public async Task Finds_implementations_of_a_member_of_a_project_carrying_stub_generation()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var implementations = await NavigationService.FindImplementationsAsync(
			snapshot,
			new SymbolTarget { FilePath = fixture.Path("XamlStub", "Ui", "Greeter.cs"), Line = 11, Column = 9 },
			200,
			TestContext.Current!.Execution.CancellationToken);

		implementations.Matches.ShouldContain(match => match.Signature.Contains("Greeter.Greet", StringComparison.Ordinal));
	}

	private static async Task<DiagnosticsResult> DiagnoseAsync(WorkspaceSession session)
	{
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		return await new DiagnosticsService(NullLogger<DiagnosticsService>.Instance).AnalyseAsync(
			snapshot, new DiagnosticsRequest(), TestContext.Current!.Execution.CancellationToken);
	}

	/// <summary>
	/// The stub generator has to sit beside the worker, because that is where the worker looks for it:
	/// <c>AppContext.BaseDirectory</c> plus the file name, loaded as an <c>AnalyzerFileReference</c>.
	/// <para>
	/// Asserted because losing it is quiet in every direction. The worker treats a missing generator as
	/// an enhancement it can do without, so it logs a warning and loads every XAML project unstubbed --
	/// which presents as thousands of phantom errors about <c>InitializeComponent</c> and
	/// <c>x:Name</c> fields in somebody's own app rather than as a file that did not ship. And the
	/// reference that brings it here compiles nothing, so no build breaks if it stops copying.
	/// </para>
	/// <para>
	/// Here rather than in the fast suite, and off this project's own output rather than the worker's:
	/// the unit test project references the generator directly for the stub tests, so the file is in its
	/// directory whatever the worker's project file says and the assertion could not fail. This project
	/// gets it only by the copy under test.
	/// </para>
	/// </summary>
	[Test]
	public void The_xaml_stub_generator_travels_with_the_worker()
	{
		var worker = typeof(SolutionLoader).Assembly.Location;
		var generator = Path.Combine(Path.GetDirectoryName(worker)!, "RoseMcp.XamlStubs.dll");

		File.Exists(generator).ShouldBeTrue(
			$"the worker loads the stub generator from its own directory, and {generator} is not there");
	}
}
