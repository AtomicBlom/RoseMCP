using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.XamlStubs;

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

		namespace Windows.UI.Xaml.Media.Animation
		{
			public class Storyboard : Windows.UI.Xaml.DependencyObject { }
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
	public void Ignores_names_inside_templates_but_not_inside_visual_states_or_resources()
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
						<Button x:Name="NamedResource" />
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

		// A named resource does get one; the real generator emits those.
		Assert.Contains("Button NamedResource;", emission.Source!, StringComparison.Ordinal);

		// A name inside a template belongs to that template's namescope, and gets nothing.
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

		var emission = XamlStubEmitter.Emit(Compile(FakeFramework), WindowsXamlDialect.Uwp, document);

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

	/// <summary>
	/// The root element is the class and also a field when it is named. Found by comparing against
	/// the real generated files, which emit one -- 'root', 'view' and the like were most of the
	/// fields an earlier version of this missed.
	/// </summary>
	[Fact]
	public void Emits_a_field_for_a_named_root_element()
	{
		var markup = """
			<UserControl x:Class="App.Widget" x:Name="shell"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		Assert.Contains("private global::Windows.UI.Xaml.Controls.UserControl shell;", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// A keyed resource has no name and no field, but a named one does -- storyboards are declared
	/// this way, and skipping resources wholesale accounted for 65 errors in a real project.
	/// </summary>
	[Fact]
	public void Emits_fields_for_named_resources_but_not_keyed_ones()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<UserControl.Resources>
					<Storyboard x:Name="FadeIn" />
					<Storyboard x:Key="Unnamed" />
				</UserControl.Resources>
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		Assert.Contains("Storyboard FadeIn;", emission.Source!, StringComparison.Ordinal);
		Assert.DoesNotContain("Unnamed", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// x:FieldModifier is how markup lets another class touch the field. Emitting it private anyway
	/// turns a working reference into CS0122, which is what happened before this.
	/// </summary>
	[Fact]
	public void Honours_the_field_modifier_the_markup_asked_for()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Button x:Name="Shared" x:FieldModifier="internal" />
				<Button x:Name="Own" />
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		Assert.Contains("internal global::Windows.UI.Xaml.Controls.Button Shared;", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("private global::Windows.UI.Xaml.Controls.Button Own;", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// The App class carries the entry point the markup compiler would have generated, so without it
	/// an application project is CS5001 -- but a hand-written Main always wins.
	/// </summary>
	[Fact]
	public void Gives_an_application_an_entry_point_unless_it_has_one()
	{
		var markup = """
			<Application x:Class="App.Program"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""";

		var behind = "namespace App { partial class Program { } }";
		var withMain = "namespace App { partial class Program { public static void Main(string[] args) { } } }";

		Assert.Contains(
			"public static void Main(string[] args)",
			Emit(markup, behind, OutputKind.WindowsApplication).Source!,
			StringComparison.Ordinal);

		Assert.DoesNotContain(
			"static void Main",
			Emit(markup, withMain, OutputKind.WindowsApplication).Source!,
			StringComparison.Ordinal);

		// A library needs no entry point at all.
		Assert.DoesNotContain("static void Main", Emit(markup, behind).Source!, StringComparison.Ordinal);
	}

	private static XamlStubEmission Emit(
		string markup,
		string codeBehind,
		OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
	{
		var document = XamlDocumentReader.Read("Widget.xaml", markup);

		Assert.NotNull(document);

		return XamlStubEmitter.Emit(
			Compile(outputKind, FakeFramework, codeBehind), WindowsXamlDialect.Uwp, document);
	}

	/// <summary>
	/// Nullable on and every warning an error, which is how the repositories this has to work in are
	/// built. A stub that only compiles under lenient settings is no use.
	/// </summary>
	private static Compilation Compile(params string[] sources) =>
		Compile(OutputKind.DynamicallyLinkedLibrary, sources);

	private static Compilation Compile(OutputKind outputKind, params string[] sources) => CSharpCompilation.Create(
		"XamlStubTests",
		sources.Select(source => CSharpSyntaxTree.ParseText(source)),
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(
			outputKind,
			nullableContextOptions: NullableContextOptions.Enable,
			generalDiagnosticOption: ReportDiagnostic.Error));
}
