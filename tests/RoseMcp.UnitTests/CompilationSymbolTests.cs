using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reconciling a symbol one compilation produced with the compilation being asked about it.
/// <para>
/// The arrangement here is the one that breaks in practice and that a pair of fixture projects does
/// not: the asking compilation reaches the type through a compiled image, while the symbol in hand
/// came from the source compilation that produced it. Two assembly symbols, one assembly identity --
/// which Roslyn answers by throwing rather than by saying no.
/// </para>
/// </summary>
public sealed class CompilationSymbolTests
{
	private const string WidgetSource = """
		namespace Probe;

		public sealed class Widget
		{
			public int Size { get; set; }
		}
		""";

	/// <summary>
	/// The failure this exists for, stated as the precondition rather than trusted to stay true
	/// silently: asking a compilation about another one's symbol is not a question it answers false,
	/// it is one it refuses -- naming a parameter no caller of a rose_* tool ever sent.
	/// </summary>
	[Test]
	public void Roslyn_refuses_a_symbol_the_asking_compilation_does_not_hold()
	{
		var (declaring, asking, widget) = TwoCompilations();

		CompilationSymbols.Holds(asking, widget).ShouldBeFalse();

		var refusal = Should.Throw<ArgumentException>(
			() => asking.IsSymbolAccessibleWithin(widget, asking.Assembly)).ShouldBeOfType<ArgumentException>();

		refusal.Message.ShouldContain("must be a symbol from this compilation", Case.Sensitive);
		CompilationSymbols.Holds(declaring, widget).ShouldBeTrue();
	}

	/// <summary>
	/// And the answer: the same type, as the asking compilation sees it, which is a symbol it will
	/// answer questions about.
	/// </summary>
	[Test]
	public void A_symbol_from_another_compilation_is_mapped_into_the_asking_one()
	{
		var (_, asking, widget) = TwoCompilations();

		var here = CompilationSymbols.AsSeenBy(asking, widget, TestContext.Current!.Execution.CancellationToken);

		here.ShouldNotBeNull();
		here!.ToDisplayString().ShouldBe("Probe.Widget");
		CompilationSymbols.Holds(asking, here).ShouldBeTrue();
		asking.IsSymbolAccessibleWithin(here, asking.Assembly).ShouldBeTrue();
	}

	/// <summary>
	/// Mapping is not filtering, and the difference is the whole reason for it: a type the asking
	/// compilation genuinely reaches must come back, or the tool reports a type in a project this one
	/// does not reference about a type it references.
	/// </summary>
	[Test]
	public void A_symbol_nothing_references_maps_to_nothing()
	{
		var (_, _, widget) = TwoCompilations();
		var stranger = Compile("Stranger", "namespace Other; public sealed class Thing;");

		CompilationSymbols.AsSeenBy(
			stranger, widget, TestContext.Current!.Execution.CancellationToken).ShouldBeNull();
	}

	/// <summary>One the compilation already holds is handed back, without a symbol key being resolved.</summary>
	[Test]
	public void A_symbol_the_compilation_already_holds_is_returned_as_it_is()
	{
		var (declaring, _, widget) = TwoCompilations();

		CompilationSymbols.AsSeenBy(declaring, widget, TestContext.Current!.Execution.CancellationToken).ShouldBeSameAs(
			widget);
	}

	/// <summary>
	/// A compilation that declares the type, one that reaches it only as compiled metadata, and the
	/// declaring compilation's own symbol for it.
	/// </summary>
	private static (Compilation Declaring, Compilation Asking, INamedTypeSymbol Widget) TwoCompilations()
	{
		var declaring = Compile("Probe", WidgetSource);

		using var image = new MemoryStream();
		var emitted = declaring.Emit(image);
		emitted.Success.ShouldBeTrue(string.Join("; ", emitted.Diagnostics.Select(d => d.ToString())));

		var asking = Compile(
			"Asking",
			"namespace Consumer; public sealed class Uses;",
			MetadataReference.CreateFromImage(image.ToArray()));

		var widget = declaring.GetTypeByMetadataName("Probe.Widget");
		widget.ShouldNotBeNull();

		return (declaring, asking, widget!);
	}

	/// <summary>
	/// Compiled against everything this test host runs on, which is the cheap way to a compilation
	/// that emits: the type under test needs a real assembly identity, not a syntax tree.
	/// </summary>
	private static CSharpCompilation Compile(string name, string source, params MetadataReference[] references)
	{
		var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

		return CSharpCompilation.Create(
			name,
			[CSharpSyntaxTree.ParseText(source)],
			[.. platform, .. references],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
	}
}
