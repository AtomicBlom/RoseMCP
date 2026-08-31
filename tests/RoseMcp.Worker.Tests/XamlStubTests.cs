using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.Worker.Xaml;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// The XAML stub core, tested against a compilation that declares its own fake Windows.UI.Xaml
/// types. No Windows SDK, no UWP project, no build -- which keeps these fast and portable while
/// still exercising the only thing that matters: does the code-behind bind afterwards.
/// </summary>
public sealed class XamlStubTests
{
	/// <summary>Enough of the framework for elements to resolve, in the namespaces UWP puts them in.</summary>
	private const string FakeFramework = """
		namespace Windows.UI.Xaml
		{
			public class DependencyObject { }
			public class FrameworkElement : DependencyObject { }
			public class VisualState : DependencyObject { }
			public class VisualStateGroup : DependencyObject { }
		}

		namespace Windows.UI.Xaml.Controls
		{
			public class Control : Windows.UI.Xaml.FrameworkElement { }
			public class UserControl : Control { }
			public class Page : Control { }
			public class Button : Control { public string Label = ""; }
			public class Grid : Control { }
		}

		namespace Windows.UI.Xaml.Controls.Primitives
		{
			public class RepeatButton : Windows.UI.Xaml.Controls.Control { }
		}

		namespace House.Controls
		{
			public class FancyThing : Windows.UI.Xaml.Controls.Control { }
		}
		""";

	[Fact]
	public void Declares_the_base_type_and_a_field_for_every_named_element()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				xmlns:house="using:House.Controls">
				<Grid x:Name="Root">
					<Button x:Name="Go" />
					<house:FancyThing x:Name="Custom" />
				</Grid>
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		Assert.Null(emission.SkipReason);
		Assert.Contains("partial class Widget : global::Windows.UI.Xaml.Controls.UserControl", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("private global::Windows.UI.Xaml.Controls.Grid Root;", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("private global::Windows.UI.Xaml.Controls.Button Go;", emission.Source!, StringComparison.Ordinal);

		// Resolved out of the type universe, so an in-house control works exactly like a built-in one.
		Assert.Contains("private global::House.Controls.FancyThing Custom;", emission.Source!, StringComparison.Ordinal);
		Assert.Empty(emission.UnresolvedTypes);
	}

	/// <summary>
	/// The strongest assertion available: put the stub back into the compilation and require that
	/// code-behind using those members compiles, with nullable on and every warning an error.
	/// </summary>
	[Fact]
	public void The_stub_compiles_where_warnings_are_errors()
	{
		var markup = """
			<Page x:Class="App.Screen"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Button x:Name="Save" />
			</Page>
			""";

		var behind = """
			namespace App
			{
				partial class Screen
				{
					public Screen() => InitializeComponent();

					public void Use() => Save.Label = "x";

					partial void UnloadObject(Windows.UI.Xaml.DependencyObject unloadableObject) { }
				}
			}
			""";

		var emission = Emit(markup, behind);
		var complete = Compile(FakeFramework, behind, emission.Source!);

		Assert.Empty(complete.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
	}

	[Fact]
	public void Ignores_names_inside_templates_but_not_inside_visual_states()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Grid>
					<VisualStateManager.VisualStateGroups>
						<VisualStateGroup x:Name="Sizes">
							<VisualState x:Name="Narrow" />
						</VisualStateGroup>
					</VisualStateManager.VisualStateGroups>
					<Grid.Resources>
						<Button x:Name="NotAField" />
					</Grid.Resources>
					<DataTemplate>
						<Button x:Name="AlsoNotAField" />
					</DataTemplate>
				</Grid>
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		// Named visual states do get fields; the real generator emits them.
		Assert.Contains("VisualStateGroup Sizes;", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("VisualState Narrow;", emission.Source!, StringComparison.Ordinal);

		// Names inside a template or a resource dictionary belong to another namescope.
		Assert.DoesNotContain("NotAField", emission.Source!, StringComparison.Ordinal);
		Assert.DoesNotContain("AlsoNotAField", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// A control we cannot see gets no field and a note saying so. Inventing a type would trade one
	/// honest error for a scattering of misleading ones.
	/// </summary>
	[Fact]
	public void Omits_what_it_cannot_resolve_and_says_what_that_was()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				xmlns:gone="using:Nowhere.Controls">
				<gone:Missing x:Name="Absent" />
				<Button x:Name="Present" />
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		Assert.DoesNotContain("Absent", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("Button Present;", emission.Source!, StringComparison.Ordinal);

		var unresolved = Assert.Single(emission.UnresolvedTypes);

		Assert.Contains("Absent", unresolved, StringComparison.Ordinal);
		Assert.Contains("Nowhere.Controls", unresolved, StringComparison.Ordinal);
	}

	[Fact]
	public void Emits_nothing_when_the_real_partial_is_already_there()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { public void InitializeComponent() { } } }");

		Assert.Null(emission.Source);
		Assert.Contains("already in the compilation", emission.SkipReason!, StringComparison.Ordinal);
	}

	/// <summary>Two partials naming different base classes is CS0263, so the other part wins.</summary>
	[Fact]
	public void Leaves_the_base_type_alone_when_the_code_behind_declares_one()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""";

		var emission = Emit(
			markup,
			"namespace App { partial class Widget : Windows.UI.Xaml.Controls.Page { } }");

		Assert.Contains("partial class Widget\n", emission.Source!, StringComparison.Ordinal);
		Assert.DoesNotContain("UserControl", emission.Source!, StringComparison.Ordinal);
	}

	[Fact]
	public void Stubs_the_bindings_member_only_when_the_markup_uses_compiled_bindings()
	{
		var withBind = """
			<UserControl x:Class="App.Bound"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Button Label="{x:Bind Title}" />
			</UserControl>
			""";

		var without = withBind.Replace("{x:Bind Title}", "plain", StringComparison.Ordinal);

		Assert.Contains("Bindings", Emit(withBind, "namespace App { partial class Bound { } }").Source!, StringComparison.Ordinal);
		Assert.DoesNotContain("Bindings", Emit(without, "namespace App { partial class Bound { } }").Source!, StringComparison.Ordinal);
	}

	[Fact]
	public void Reads_nothing_useful_out_of_markup_with_no_code_behind()
	{
		var markup = """
			<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
			""";

		var document = XamlDocumentReader.Read("Theme.xaml", markup);

		Assert.NotNull(document);
		Assert.Null(document.ClassName);

		var emission = XamlStubEmitter.Emit(Compile(FakeFramework), new UwpXamlDialect(), document);

		Assert.Contains("no x:Class", emission.SkipReason!, StringComparison.Ordinal);
	}

	[Fact]
	public void Survives_markup_that_is_not_valid_xml()
	{
		Assert.Null(XamlDocumentReader.Read("Broken.xaml", "<UserControl <<< />"));
	}

	/// <summary>The dialect is chosen from what the project references, not from the markup.</summary>
	[Fact]
	public void Picks_the_dialect_from_the_referenced_framework()
	{
		var chosen = XamlDialectSelector.Select(Compile(FakeFramework), []);

		Assert.NotNull(chosen.Dialect);
		Assert.Equal("UWP", chosen.Dialect.Name);
		Assert.False(chosen.WasAmbiguous);
		Assert.Contains("Windows.UI.Xaml.Controls.Control", chosen.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void Reports_no_dialect_when_no_framework_is_referenced()
	{
		var chosen = XamlDialectSelector.Select(Compile("namespace Plain { public class Thing { } }"), []);

		Assert.Null(chosen.Dialect);
		Assert.Contains("no XAML framework", chosen.Reason, StringComparison.Ordinal);
	}

	private static XamlStubEmission Emit(string markup, string codeBehind)
	{
		var document = XamlDocumentReader.Read("Widget.xaml", markup);

		Assert.NotNull(document);

		return XamlStubEmitter.Emit(Compile(FakeFramework, codeBehind), new UwpXamlDialect(), document);
	}

	/// <summary>
	/// Nullable on and every warning an error, which is how the repositories this has to work in are
	/// built. A stub that only compiles under lenient settings is no use.
	/// </summary>
	private static Compilation Compile(params string[] sources) => CSharpCompilation.Create(
		"XamlStubTests",
		sources.Select(source => CSharpSyntaxTree.ParseText(source)),
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(
			OutputKind.DynamicallyLinkedLibrary,
			nullableContextOptions: NullableContextOptions.Enable,
			generalDiagnosticOption: ReportDiagnostic.Error));
}
