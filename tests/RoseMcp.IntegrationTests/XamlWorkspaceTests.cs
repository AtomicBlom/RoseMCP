using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.Worker.Xaml;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The whole pipeline over a real solution: find the markup, add it as additional documents, run the
/// generator, and have the code-behind bind. The fixture declares its own Windows.UI.Xaml types, so
/// this needs no Windows SDK and no UWP tooling -- only the shape of the problem, not its scale.
/// </summary>
public sealed class XamlWorkspaceTests
{
	[Fact]
	public async Task A_xaml_project_compiles_without_its_markup_compiler_ever_running()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var diagnostics = await DiagnoseAsync(session);

		// Without the stub, this project has an unresolved InitializeComponent and an unknown Save.
		Assert.Empty(diagnostics.Diagnostics);
	}

	/// <summary>
	/// The stub has to be a source-generated document, not a file: readable through the generated
	/// document tools, and never written to disk beside the user's code.
	/// </summary>
	[Fact]
	public async Task The_stub_is_generated_code_and_stays_out_of_the_tree()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var generated = await GeneratedDocumentService.ListAsync(
			snapshot, null, TestContext.Current.CancellationToken);

		var stub = Assert.Single(generated.Documents, document =>
			document.HintName.Contains("xamlstub", StringComparison.OrdinalIgnoreCase));

		Assert.Contains("Widget", stub.HintName, StringComparison.Ordinal);
		Assert.False(File.Exists(fixture.Path("XamlStub", "Ui", "Widget.xamlstub.g.cs")));

		var content = await GeneratedDocumentService.ReadAsync(
			snapshot, stub.HintName, null, TestContext.Current.CancellationToken);

		Assert.Contains("partial class Widget : global::Windows.UI.Xaml.Controls.UserControl", content.Text, StringComparison.Ordinal);
		Assert.Contains("private global::Windows.UI.Xaml.Controls.Button Save;", content.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Reports_which_dialect_it_chose_and_on_what_evidence()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// Generators are lazy; asking for the compilation is what runs them.
		await DiagnoseAsync(session);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var project = snapshot.Solution.Projects.Single(candidate => candidate.Name.StartsWith("Ui", StringComparison.Ordinal));
		var report = await XamlStubReportReader.ReadAsync(project, TestContext.Current.CancellationToken);

		Assert.NotNull(report);
		Assert.Equal("UWP", report.Dialect);
		Assert.False(report.DialectAmbiguous);
		Assert.Contains("Windows.UI.Xaml.Controls.Control", report.DialectReason, StringComparison.Ordinal);
		Assert.Equal(1, report.MarkupFileCount);
		Assert.Equal(1, report.StubbedClassCount);
		Assert.Empty(report.UnresolvedTypes);
	}

	/// <summary>
	/// Markup is tracked like any other file, so editing a .xaml behind the workspace's back changes
	/// the generated partial on the next read -- no reload, no refresh call.
	/// </summary>
	[Fact]
	public async Task Picks_up_a_new_named_element_when_the_markup_changes_on_disk()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var markupPath = fixture.Path("XamlStub", "Ui", "Widget.xaml");
		var markup = await File.ReadAllTextAsync(markupPath, TestContext.Current.CancellationToken);

		await File.WriteAllTextAsync(
			markupPath,
			markup.Replace(
				"<Button x:Name=\"Save\" Label=\"Save\" />",
				"<Button x:Name=\"Save\" Label=\"Save\" />\r\n\t\t<Button x:Name=\"Cancel\" />",
				StringComparison.Ordinal),
			TestContext.Current.CancellationToken);

		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var generated = await GeneratedDocumentService.ListAsync(
			snapshot, null, TestContext.Current.CancellationToken);

		var stub = Assert.Single(generated.Documents, document =>
			document.HintName.Contains("xamlstub", StringComparison.OrdinalIgnoreCase));

		var content = await GeneratedDocumentService.ReadAsync(
			snapshot, stub.HintName, null, TestContext.Current.CancellationToken);

		Assert.Contains("Button Cancel;", content.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Leaves_the_workspace_alone_when_stubs_are_turned_off()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");

		var loader = new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance);

		var load = await loader.LoadAsync(
			new WorkerOptions { SolutionPath = fixture.SolutionPath, NoXamlStubs = true },
			TestContext.Current.CancellationToken);

		try
		{
			var project = load.Solution.Projects.Single();

			Assert.Empty(project.AdditionalDocuments);
			Assert.Empty(await project.GetSourceGeneratedDocumentsAsync(TestContext.Current.CancellationToken));
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
	[Fact]
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

		await host.StartAsync(TestContext.Current.CancellationToken);

		var status = await host.GetStatusAsync(TestContext.Current.CancellationToken);
		var project = Assert.Single(status.Projects);

		Assert.Equal(1, project.XamlMarkupCount);
		Assert.Equal(1, project.XamlStubbedCount);
		Assert.Equal("UWP", project.XamlDialect);
		Assert.Empty(project.UnresolvedXamlTypes);

		// Stubbing successfully is not a reason to call the workspace degraded.
		Assert.Empty(status.DegradedReasons);
		Assert.Equal(WorkspaceState.Loaded, status.State);
	}

	/// <summary>The host stops the process when a solution vanishes; a test has nothing to stop.</summary>
	private sealed class NeverStops : Microsoft.Extensions.Hosting.IHostApplicationLifetime
	{
		public CancellationToken ApplicationStarted => CancellationToken.None;

		public CancellationToken ApplicationStopping => CancellationToken.None;

		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication()
		{
		}
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
	[Fact]
	public async Task Finds_references_to_a_member_of_a_project_carrying_stub_generation()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			fixture.Path("XamlStub", "Ui", "Greeter.cs"),
			16,
			24,
			200,
			TestContext.Current.CancellationToken);

		Assert.Contains("Greeter.Greet", references.Symbol, StringComparison.Ordinal);
		Assert.Contains(references.References, reference => reference.Line == 21);
	}

	/// <summary>The other tool that reaches the same index, by the same route.</summary>
	[Fact]
	public async Task Finds_implementations_of_a_member_of_a_project_carrying_stub_generation()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var implementations = await NavigationService.FindImplementationsAsync(
			snapshot,
			fixture.Path("XamlStub", "Ui", "Greeter.cs"),
			11,
			9,
			200,
			TestContext.Current.CancellationToken);

		Assert.Contains(implementations.Matches, match => match.Signature.Contains("Greeter.Greet", StringComparison.Ordinal));
	}

	private static async Task<DiagnosticsResult> DiagnoseAsync(WorkspaceSession session)
	{
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		return await new DiagnosticsService(NullLogger<DiagnosticsService>.Instance).AnalyseAsync(
			snapshot, new DiagnosticsRequest(), TestContext.Current.CancellationToken);
	}
}
