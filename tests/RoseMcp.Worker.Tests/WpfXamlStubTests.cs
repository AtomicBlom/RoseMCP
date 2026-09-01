using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.XamlStubs;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// The WPF dialect, against a compilation declaring its own fake System.Windows types. Every
/// expectation here was taken from the .g.i.cs files a real WPF build left in obj, not from
/// reasoning about what the markup compiler probably does.
/// </summary>
public sealed class WpfXamlStubTests
{
	/// <summary>Enough of WPF for elements to resolve, in the namespaces PresentationFramework puts them in.</summary>
	private const string FakeFramework = """
		namespace System.Windows
		{
			public class DependencyObject { }
			public class FrameworkElement : DependencyObject { }
			public class Window : FrameworkElement { }
			public class Application { }
			public class Style { }
		}

		namespace System.Windows.Controls
		{
			public class Control : System.Windows.FrameworkElement { }
			public class UserControl : Control { }
			public class ContentControl : Control { }
			public class Image : Control { }
			public class Grid : Control { }
			public class Button : Control { }
		}

		namespace System.Windows.Controls.Primitives
		{
			public class Popup : System.Windows.Controls.Control { }
		}

		namespace System.Windows.Media
		{
			public class RotateTransform : System.Windows.DependencyObject { }
		}

		namespace House.Views
		{
			public class Crumb : System.Windows.Controls.Control { }
		}
		""";

	[Fact]
	public void Declares_the_base_type_and_an_internal_field_for_every_named_element()
	{
		var markup = """
			<Window x:Class="App.ShellView"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				xmlns:house="clr-namespace:House.Views;assembly=House">
				<Grid>
					<Image x:Name="LoginImage" />
					<ContentControl x:Name="ActiveItem" />
					<house:Crumb x:Name="Crumb" />
				</Grid>
			</Window>
			""";

		var emission = Emit(markup, "namespace App { partial class ShellView { } }");

		Assert.Null(emission.SkipReason);
		Assert.Contains("partial class ShellView : global::System.Windows.Window", emission.Source!, StringComparison.Ordinal);

		// Internal is WPF's default, where the Windows frameworks use private. Emitting private here
		// turns a working reference from another class into CS0122.
		Assert.Contains("internal global::System.Windows.Controls.Image LoginImage;", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("internal global::System.Windows.Controls.ContentControl ActiveItem;", emission.Source!, StringComparison.Ordinal);

		// clr-namespace: is WPF's form of the same thing UWP writes as using:, assembly= and all.
		Assert.Contains("internal global::House.Views.Crumb Crumb;", emission.Source!, StringComparison.Ordinal);
		Assert.Empty(emission.UnresolvedTypes);
	}

	[Fact]
	public void Resolves_a_framework_element_to_the_namespace_the_markup_compiler_chose()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Grid>
					<Popup x:Name="ExclusionPopup" />
					<Grid.RenderTransform>
						<RotateTransform x:Name="DontFreeze" />
					</Grid.RenderTransform>
				</Grid>
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		// Both were reached through a namespace that is not the first candidate, which is what the
		// dialect's ordering is for. A property element is not a type and contributes no field.
		Assert.Contains(
			"internal global::System.Windows.Controls.Primitives.Popup ExclusionPopup;",
			emission.Source!,
			StringComparison.Ordinal);

		Assert.Contains(
			"internal global::System.Windows.Media.RotateTransform DontFreeze;",
			emission.Source!,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// WPF types a named root as the class, where UWP types it as the element -- both read off real
	/// generated files. Typing it as the element would compile and then fail on every member of the
	/// view itself, which is a worse failure than the missing field it replaced.
	/// </summary>
	[Fact]
	public void Types_a_named_root_element_as_the_class_it_generates()
	{
		var markup = """
			<UserControl x:Class="App.ProjectSelectionView" x:Name="ProjectSelectionRoot"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""";

		var behind = """
			namespace App
			{
				partial class ProjectSelectionView
				{
					internal string Title => "t";
				}
			}
			""";

		var emission = Emit(markup, behind);

		Assert.Contains(
			"internal global::App.ProjectSelectionView ProjectSelectionRoot;",
			emission.Source!,
			StringComparison.Ordinal);

		Assert.DoesNotContain("UserControl ProjectSelectionRoot", emission.Source!, StringComparison.Ordinal);
	}

	[Fact]
	public void Honours_an_explicit_field_modifier_over_the_dialect_default()
	{
		var markup = """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Button x:Name="Shared" x:FieldModifier="public" />
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Widget { } }");

		Assert.Contains("public global::System.Windows.Controls.Button Shared;", emission.Source!, StringComparison.Ordinal);
	}

	[Fact]
	public void Gives_an_application_definition_the_entry_point_its_markup_compiler_would_have()
	{
		var markup = """
			<Application x:Class="App.WizardApp"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				Startup="Application_Startup" />
			""";

		var behind = """
			namespace App
			{
				partial class WizardApp : global::System.Windows.Application
				{
					private void Application_Startup(object sender, object e) { }
				}
			}
			""";

		var emission = Emit(markup, behind, OutputKind.WindowsApplication);

		Assert.Contains("static void Main", emission.Source!, StringComparison.Ordinal);

		// The code-behind already declares the base, and a second one would be CS0263.
		Assert.DoesNotContain(": global::System.Windows.Application", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// The strongest assertion available: put the stub back in and require that code-behind using
	/// those members compiles, with nullable on and every warning an error. WPF assigns its named
	/// fields from generated code this deliberately does not emit, so it is also what proves the
	/// pragmas cover the never-assigned warning that leaves behind.
	/// </summary>
	[Fact]
	public void Leaves_a_compilation_that_binds_and_reports_nothing()
	{
		var markup = """
			<Window x:Class="App.ShellView"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<ContentControl x:Name="ActiveItem" />
			</Window>
			""";

		var behind = """
			namespace App
			{
				partial class ShellView
				{
					public ShellView()
					{
						InitializeComponent();
					}

					public object Current => ActiveItem;
				}
			}

			namespace App.Elsewhere
			{
				internal static class Reader
				{
					// Internal, so this compiles. Private -- the Windows frameworks' default -- is CS0122.
					internal static object From(global::App.ShellView view) => view.ActiveItem;
				}
			}
			""";

		var document = XamlDocumentReader.Read("ShellView.xaml", markup);
		Assert.NotNull(document);

		var emission = XamlStubEmitter.Emit(
			Compile(OutputKind.DynamicallyLinkedLibrary, FakeFramework, behind), WpfXamlDialect.Instance, document);

		Assert.NotNull(emission.Source);

		var complete = Compile(OutputKind.DynamicallyLinkedLibrary, FakeFramework, behind, emission.Source);

		Assert.Empty(complete.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
	}

	/// <summary>
	/// An SDK-style .NET Framework project defaults to C# 7.3, and WPF projects are the ones most
	/// likely to still be on it. Found by loading a real net48 WPF project, where the stub's own
	/// #nullable disable was three CS8370 errors in a file whose whole purpose is removing errors.
	/// </summary>
	[Fact]
	public void Emits_nothing_a_pre_nullable_language_version_cannot_parse()
	{
		var markup = """
			<Window x:Class="App.ShellView"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<ContentControl x:Name="ActiveItem" />
			</Window>
			""";

		var behind = """
			namespace App
			{
				partial class ShellView
				{
					public ShellView()
					{
						InitializeComponent();
					}

					public object Current { get { return ActiveItem; } }
				}
			}
			""";

		var document = XamlDocumentReader.Read("ShellView.xaml", markup);
		Assert.NotNull(document);

		var emission = XamlStubEmitter.Emit(
			Compile(LanguageVersion.CSharp7_3, FakeFramework, behind), WpfXamlDialect.Instance, document);

		Assert.NotNull(emission.Source);
		Assert.DoesNotContain("#nullable", emission.Source, StringComparison.Ordinal);

		// And it still has to compile, which is the assertion that would have caught this.
		var complete = Compile(LanguageVersion.CSharp7_3, FakeFramework, behind, emission.Source);

		Assert.Empty(complete
			.GetDiagnostics(TestContext.Current.CancellationToken)
			.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
	}

	[Fact]
	public void Recognises_a_wpf_project_by_the_types_it_references()
	{
		var document = XamlDocumentReader.Read("Widget.xaml", """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""");

		var choice = XamlDialectSelector.Select(
			Compile(OutputKind.DynamicallyLinkedLibrary, FakeFramework), [document!]);

		Assert.Same(WpfXamlDialect.Instance, choice.Dialect);
		Assert.False(choice.WasAmbiguous);
	}

	private static XamlStubEmission Emit(
		string markup,
		string codeBehind,
		OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
	{
		var document = XamlDocumentReader.Read("Widget.xaml", markup);

		Assert.NotNull(document);

		return XamlStubEmitter.Emit(
			Compile(outputKind, FakeFramework, codeBehind), WpfXamlDialect.Instance, document);
	}

	/// <summary>
	/// Nullable cannot be switched on below C# 8, so this pins the version and leaves the rest at the
	/// same warnings-as-errors strictness as the other tests.
	/// </summary>
	private static Compilation Compile(LanguageVersion language, params string[] sources) => CSharpCompilation.Create(
		"WpfXamlStubTests",
		sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(language))),
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(
			OutputKind.DynamicallyLinkedLibrary,
			generalDiagnosticOption: ReportDiagnostic.Error));

	private static Compilation Compile(OutputKind outputKind, params string[] sources) => CSharpCompilation.Create(
		"WpfXamlStubTests",
		sources.Select(source => CSharpSyntaxTree.ParseText(source)),
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(
			outputKind,
			nullableContextOptions: NullableContextOptions.Enable,
			generalDiagnosticOption: ReportDiagnostic.Error));
}
