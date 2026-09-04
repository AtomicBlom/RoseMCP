using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.XamlStubs;

namespace RoseMcp.UnitTests;

/// <summary>
/// The WinUI dialect, against a compilation declaring its own fake Microsoft.UI.Xaml types.
/// <para>
/// Every expectation here was read off the .g.i.cs files a real WinUI 3 build left in obj -- this
/// repository's own RoseMcp.Tray, which is a WinUI 3 app and so is the probe -- rather than carried
/// over from UWP on the assumption that the two agree. They mostly do, and the one value worth
/// doubting is <see cref="IXamlDialect.RootFieldIsTheClass"/>: it differs between WPF and UWP for
/// no reason either would suggest, so WinUI's was measured with a named root rather than inherited.
/// </para>
/// <para>
/// Worth knowing while reading these: on an SDK-style WinUI 3 project none of this runs. The markup
/// compiler takes part in the design-time build, so <c>InitializeComponent</c> is already in the
/// compilation and the emitter skips -- verified by deleting the generated partials and watching a
/// reload put them back. These cover the fallback, which is what is left when the XAML compiler
/// does not run.
/// </para>
/// </summary>
public sealed class WinUiXamlStubTests
{
	/// <summary>
	/// Enough of WinUI for elements to resolve, in the namespaces the WindowsAppSDK puts them in.
	/// Window sits directly in Microsoft.UI.Xaml and is not a FrameworkElement, which is WinUI 3's
	/// own shape and not a simplification.
	/// </summary>
	private const string FakeFramework = """
		namespace Microsoft.UI.Xaml
		{
			public class DependencyObject { }
			public class UIElement : DependencyObject { }
			public class FrameworkElement : UIElement { }
			public class Window { }
			public class Application { }
		}

		namespace Microsoft.UI.Xaml.Controls
		{
			public class Control : Microsoft.UI.Xaml.FrameworkElement { }
			public class UserControl : Control { }
			public class Grid : Microsoft.UI.Xaml.FrameworkElement { }
			public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { }
			public class Image : Microsoft.UI.Xaml.FrameworkElement { }
			public class ScrollViewer : Control { }
			public class ItemsRepeater : Microsoft.UI.Xaml.FrameworkElement { }
		}

		namespace Microsoft.UI.Xaml.Controls.Primitives
		{
			public class Thumb : Microsoft.UI.Xaml.Controls.Control { }
		}

		namespace H.NotifyIcon
		{
			public class TaskbarIcon : Microsoft.UI.Xaml.FrameworkElement { }
		}
		""";

	[Fact]
	public void Recognises_a_winui_project_by_the_types_it_references()
	{
		var document = XamlDocumentReader.Read("Widget.xaml", """
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""");

		var choice = XamlDialectSelector.Select(Compile(OutputKind.DynamicallyLinkedLibrary, FakeFramework), [document!]);

		Assert.Same(WindowsXamlDialect.WinUi, choice.Dialect);
		Assert.False(choice.WasAmbiguous);
	}

	/// <summary>
	/// The shape of RoseMcp.Tray's MainWindow.g.i.cs: a Window base reached through the root
	/// namespace, private fields, and a third-party control named through a using: prefix.
	/// </summary>
	[Fact]
	public void Declares_the_base_type_and_a_private_field_for_every_named_element()
	{
		var markup = """
			<Window x:Class="App.MainWindow"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				xmlns:tb="using:H.NotifyIcon">
				<Grid x:Name="TitleBarArea">
					<tb:TaskbarIcon x:Name="Tray" />
					<ItemsRepeater x:Name="Workspaces" />
				</Grid>
			</Window>
			""";

		var emission = Emit(markup, "namespace App { partial class MainWindow { } }");

		Assert.Null(emission.SkipReason);

		// Window is in Microsoft.UI.Xaml itself, so it is only found once the two Controls
		// namespaces ahead of it in the precedence list have missed.
		Assert.Contains(
			"partial class MainWindow : global::Microsoft.UI.Xaml.Window",
			emission.Source!,
			StringComparison.Ordinal);

		// Private, which is the Windows frameworks' default where WPF generates internal.
		Assert.Contains(
			"private global::Microsoft.UI.Xaml.Controls.Grid TitleBarArea;",
			emission.Source!,
			StringComparison.Ordinal);

		Assert.Contains(
			"private global::Microsoft.UI.Xaml.Controls.ItemsRepeater Workspaces;",
			emission.Source!,
			StringComparison.Ordinal);

		// using: is the Windows form of what WPF writes as clr-namespace:.
		Assert.Contains("private global::H.NotifyIcon.TaskbarIcon Tray;", emission.Source!, StringComparison.Ordinal);
		Assert.Empty(emission.UnresolvedTypes);
	}

	/// <summary>
	/// Measured, not assumed. A UserControl named at its root generated
	/// <c>private global::Microsoft.UI.Xaml.Controls.UserControl ProbeRoot;</c> -- the element, not
	/// the class -- so WinUI agrees with UWP and differs from WPF, which types the same field as the
	/// class it generates. Getting this backwards is not cosmetic in either direction: as the class
	/// it would be a field of a type the markup never names, and WPF's own docstring records what
	/// the other mistake costs.
	/// </summary>
	[Fact]
	public void Types_a_named_root_element_as_the_element_the_markup_writes()
	{
		var markup = """
			<UserControl x:Class="App.Probe" x:Name="ProbeRoot"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Grid x:Name="ProbeGrid" />
			</UserControl>
			""";

		var emission = Emit(markup, "namespace App { partial class Probe { } }");

		Assert.Contains(
			"private global::Microsoft.UI.Xaml.Controls.UserControl ProbeRoot;",
			emission.Source!,
			StringComparison.Ordinal);

		Assert.DoesNotContain("global::App.Probe ProbeRoot", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// The members beyond the fields. UnloadObject is a declaration only, because code-behind that
	/// writes the implementing half is CS0759 without one, and its parameter is typed in this
	/// dialect's own root namespace.
	/// </summary>
	[Fact]
	public void Declares_the_unload_hook_the_markup_compiler_would_have()
	{
		var emission = Emit(
			"""
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""",
			"namespace App { partial class Widget { } }");

		Assert.Contains("private bool _contentLoaded;", emission.Source!, StringComparison.Ordinal);

		Assert.Contains(
			"partial void UnloadObject(global::Microsoft.UI.Xaml.DependencyObject unloadableObject);",
			emission.Source!,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// The Bindings member exists only where {x:Bind} does. RoseMcp.Tray shows both halves: its
	/// MainWindow uses compiled bindings and got the interface and the field, its App.xaml uses none
	/// and got neither. Emitting it unconditionally would put a member on classes the real generator
	/// leaves alone.
	/// </summary>
	[Fact]
	public void Declares_the_compiled_binding_members_only_where_the_markup_binds()
	{
		var withBind = Emit(
			"""
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<TextBlock Text="{x:Bind Title}" />
			</UserControl>
			""",
			"namespace App { partial class Widget { public string Title => \"t\"; } }");

		Assert.Contains("private interface IWidget_Bindings", withBind.Source!, StringComparison.Ordinal);
		Assert.Contains("private IWidget_Bindings Bindings;", withBind.Source!, StringComparison.Ordinal);

		var without = Emit(
			"""
			<UserControl x:Class="App.Widget"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<TextBlock Text="{Binding Title}" />
			</UserControl>
			""",
			"namespace App { partial class Widget { } }");

		Assert.DoesNotContain("_Bindings", without.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// An application definition needs an entry point or the exe is CS5001. The real markup compiler
	/// puts Main on a separate <c>Program</c> class rather than on App; this puts it on the partial,
	/// which satisfies the compiler equally and keeps the emitter to one generated type per file.
	/// Nothing hand-written calls either.
	/// </summary>
	[Fact]
	public void Gives_an_application_definition_the_entry_point_its_markup_compiler_would_have()
	{
		var emission = Emit(
			"""
			<Application x:Class="App.TrayApp"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
			""",
			"namespace App { partial class TrayApp { } }",
			OutputKind.WindowsApplication);

		Assert.Contains("static void Main", emission.Source!, StringComparison.Ordinal);
		Assert.Contains("partial class TrayApp : global::Microsoft.UI.Xaml.Application", emission.Source!, StringComparison.Ordinal);
	}

	/// <summary>
	/// The strongest assertion available: put the stub back in and require that code-behind using
	/// those members compiles, with nullable on and every warning an error. Private fields are only
	/// reachable from the class itself, which is the difference from the WPF version of this test
	/// and the reason that one reads a field from a second class and this one does not.
	/// </summary>
	[Fact]
	public void Leaves_a_compilation_that_binds_and_reports_nothing()
	{
		var markup = """
			<Window x:Class="App.MainWindow"
				xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
				<Grid x:Name="TitleBarArea">
					<TextBlock x:Name="Headline" Text="{x:Bind Title}" />
				</Grid>
			</Window>
			""";

		var behind = """
			namespace App
			{
				partial class MainWindow
				{
					public MainWindow()
					{
						InitializeComponent();
					}

					public string Title => "t";

					public object Chrome => TitleBarArea;

					public object Text => Headline;

					// The declaration the stub emits is what lets this compile: an implementing half
					// with no defining declaration is CS0759.
					partial void UnloadObject(global::Microsoft.UI.Xaml.DependencyObject unloadableObject)
					{
					}
				}
			}
			""";

		var document = XamlDocumentReader.Read("MainWindow.xaml", markup);
		Assert.NotNull(document);

		var emission = XamlStubEmitter.Emit(
			Compile(OutputKind.DynamicallyLinkedLibrary, FakeFramework, behind), WindowsXamlDialect.WinUi, document);

		Assert.NotNull(emission.Source);

		var complete = Compile(OutputKind.DynamicallyLinkedLibrary, FakeFramework, behind, emission.Source);

		Assert.Empty(complete
			.GetDiagnostics(TestContext.Current.CancellationToken)
			.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
	}

	private static XamlStubEmission Emit(
		string markup,
		string codeBehind,
		OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
	{
		var document = XamlDocumentReader.Read("Widget.xaml", markup);

		Assert.NotNull(document);

		return XamlStubEmitter.Emit(
			Compile(outputKind, FakeFramework, codeBehind), WindowsXamlDialect.WinUi, document);
	}

	private static Compilation Compile(OutputKind outputKind, params string[] sources) => CSharpCompilation.Create(
		"WinUiXamlStubTests",
		sources.Select(source => CSharpSyntaxTree.ParseText(source)),
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(
			outputKind,
			nullableContextOptions: NullableContextOptions.Enable,
			generalDiagnosticOption: ReportDiagnostic.Error));
}
