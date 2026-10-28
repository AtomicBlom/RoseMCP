using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Worker.Xaml;
using RoseMcp.XamlStubs;

namespace RoseMcp.UnitTests;

/// <summary>
/// Markup in a dialect no stub stands in for, compiled inside the workspace by its framework's own generator -- a framework
/// whose markup has its own namespace and whose generator writes <c>InitializeComponent</c> as the project compiles.
/// Nothing needs stubbing there, and saying the code-behind reports errors that are not real is itself not true.
/// </summary>
public sealed class GeneratorCompiledXamlTests
{
	private const string Markup = """
		<StackPanel xmlns="https://example.com/game/ui"
					xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
					x:Class="Game.MainView">
			<TextBlock x:Name="Title" />
		</StackPanel>
		""";

	private const string CodeBehind = "namespace Game { public partial class MainView { public MainView() => InitializeComponent(); } }";

	private const string Generated = "namespace Game { partial class MainView { private void InitializeComponent() { } } }";

	private static readonly string GeneratedPath = Path.Combine(
		Path.GetTempPath(), "Game", "obj", "Views.SourceGen", "Views.SourceGen.XamlSourceGenerator", "Game.MainView.g.cs");

	[Test]
	public void Markup_whose_classes_a_generator_compiled_is_compiled_by_that_generator()
	{
		var compilation = Compile(CodeBehind, (Generated, GeneratedPath));

		XamlCompiledElsewhere.Find(compilation, [GeneratedPath], ["Game.MainView"]).ShouldBe("Views.SourceGen");
	}

	/// <summary>One class nothing compiles is markup nothing compiles, and the warning about it stands.</summary>
	[Test]
	public void Markup_with_one_class_no_generator_compiled_is_not()
	{
		var compilation = Compile(CodeBehind + " namespace Game { public partial class Other { } }", (Generated, GeneratedPath));

		XamlCompiledElsewhere.Find(compilation, [GeneratedPath], ["Game.MainView", "Game.Other"]).ShouldBeNull();
	}

	/// <summary>An InitializeComponent somebody wrote by hand says nothing about who compiles the markup.</summary>
	[Test]
	public void A_hand_written_initialise_is_not_a_generator_compiling_the_markup()
	{
		var handWritten = Path.Combine(Path.GetTempPath(), "Game", "MainView.Init.cs");
		var compilation = Compile(CodeBehind, (Generated, handWritten));

		XamlCompiledElsewhere.Find(compilation, [GeneratedPath], ["Game.MainView"]).ShouldBeNull();
	}

	/// <summary>
	/// With no dialect chosen, the stub generator names the classes it left alone, so the worker can ask the finished
	/// compilation about them.
	/// </summary>
	[Test]
	public void The_stub_generator_names_the_classes_it_could_not_stub()
	{
		var driver = CSharpGeneratorDriver.Create(
			[new XamlStubGenerator().AsSourceGenerator()],
			additionalTexts: [new InMemoryText("MainView.xaml", Markup)]);

		var result = driver
			.RunGenerators(Compile(CodeBehind), TestContext.Current!.Execution.CancellationToken)
			.GetRunResult();

		var report = result.GeneratedTrees.ShouldHaveSingleItem().GetText(TestContext.Current!.Execution.CancellationToken).ToString();
		report.ShouldContain("\"UnstubbedClasses\":[\"Game.MainView\"]", Case.Sensitive);
	}

	private static Compilation Compile(string code, params (string Code, string Path)[] more)
	{
		var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		SyntaxTree[] trees =
		[
			CSharpSyntaxTree.ParseText(code, path: Path.Combine(Path.GetTempPath(), "Game", "MainView.xaml.cs")),
			.. more.Select(tree => CSharpSyntaxTree.ParseText(tree.Code, path: tree.Path)),
		];

		return CSharpCompilation.Create("Game", trees, [corlib], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
	}

	private sealed class InMemoryText(string path, string text) : AdditionalText
	{
		public override string Path => path;

		public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
	}
}
