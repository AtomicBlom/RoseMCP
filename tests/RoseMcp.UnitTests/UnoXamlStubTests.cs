using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoseMcp.XamlStubs;

namespace RoseMcp.UnitTests;

/// <summary>
/// Uno Platform, which writes WinUI markup against WinUI's names but compiles it with a source
/// generator of its own. That generator runs in the workspace, so a stub on top of it duplicates
/// every named field and InitializeComponent -- hundreds of CS0102 and CS0229 in a project the real
/// build compiles clean.
/// </summary>
public sealed class UnoXamlStubTests
{
	private const string FakeFramework = """
		namespace Microsoft.UI.Xaml
		{
			public class DependencyObject { }
			public class UIElement : DependencyObject { }
			public class FrameworkElement : UIElement { }
		}

		namespace Microsoft.UI.Xaml.Controls
		{
			public class Control : Microsoft.UI.Xaml.FrameworkElement { }
			public class Page : Control { }
			public class Grid : Microsoft.UI.Xaml.FrameworkElement { }
		}
		""";

	private const string Markup = """
		<Page x:Class="App.Shell"
			xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
			xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			<Grid x:Name="BottomBarHost" />
		</Page>
		""";

	private const string CodeBehind = "namespace App { partial class Shell { } }";

	[Test]
	public void Recognises_that_uno_compiles_its_own_markup()
	{
		var document = XamlDocumentReader.Read("Shell.xaml", Markup);

		var choice = XamlDialectSelector.Select(Compile("Uno.UI"), [document!]);

		choice.Dialect.ShouldBeSameAs(WindowsXamlDialect.WinUi);
		choice.CompiledInWorkspaceBy.ShouldBe("Uno.UI");
	}

	/// <summary>
	/// The same types from the Windows App SDK, which is what Uno's Windows target references: the real
	/// markup compiler applies there, so stubbing stays on.
	/// </summary>
	[Test]
	public void Leaves_winui_proper_to_be_stubbed()
	{
		var document = XamlDocumentReader.Read("Shell.xaml", Markup);

		var choice = XamlDialectSelector.Select(Compile("Microsoft.WinUI"), [document!]);

		choice.Dialect.ShouldBeSameAs(WindowsXamlDialect.WinUi);
		choice.CompiledInWorkspaceBy.ShouldBeNull();
	}

	[Test]
	public void Generates_no_stub_for_an_uno_project_and_says_why()
	{
		var driver = CSharpGeneratorDriver.Create(
			[new XamlStubGenerator().AsSourceGenerator()],
			additionalTexts: [new InMemoryText("Shell.xaml", Markup)]);

		var result = driver
			.RunGenerators(Compile("Uno.UI"), TestContext.Current!.Execution.CancellationToken)
			.GetRunResult();

		var generated = result.GeneratedTrees.Select(tree => Path.GetFileName(tree.FilePath)).ToArray();
		generated.ShouldNotContain(name => name.Contains(".xamlstub.", StringComparison.Ordinal));

		var report = result.GeneratedTrees.Single().GetText(TestContext.Current!.Execution.CancellationToken).ToString();
		report.ShouldContain("Uno.UI", Case.Sensitive);
	}

	/// <summary>
	/// The framework as a separate assembly, because the defining assembly's name is the whole signal.
	/// </summary>
	private static Compilation Compile(string frameworkAssembly)
	{
		var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

		var framework = CSharpCompilation.Create(
			frameworkAssembly,
			[CSharpSyntaxTree.ParseText(FakeFramework)],
			[corlib],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		return CSharpCompilation.Create(
			"UnoXamlStubTests",
			[CSharpSyntaxTree.ParseText(CodeBehind)],
			[corlib, framework.ToMetadataReference()],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
	}

	private sealed class InMemoryText(string path, string text) : AdditionalText
	{
		public override string Path => path;

		public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
	}
}
