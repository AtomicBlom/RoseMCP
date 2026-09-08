using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Recognising a XAML framework from a process's loaded modules (#74).
/// <para>
/// The UWP and WinUI 3 lists below were read off real running processes -- the classic UWP probe app
/// under <c>tests/apps/uwp-classic</c>, and <c>RoseMcp.Tray</c>, which is itself a WinUI 3 app --
/// rather than reasoned about. That matters here more than usual: the issue proposing this work
/// named <c>Microsoft.UI.Xaml.dll</c> as the WinUI 3 signal, and a real WinUI 3 process does load it,
/// so the claim survives casual inspection. What it does not survive is WinUI 2, which is a UWP
/// library shipping a <c>Microsoft.UI.Xaml.dll</c> of its own.
/// </para>
/// </summary>
public sealed class XamlStackModulesTests
{
	/// <summary>Observed on the classic UWP probe app. No Microsoft.* XAML module at all.</summary>
	private static readonly string[] ClassicUwp =
	[
		"ntdll.dll",
		"System.Runtime.WindowsRuntime.UI.Xaml.dll",
		"Windows.UI.Xaml.Controls.dll",
		"Windows.UI.Xaml.dll",
	];

	/// <summary>Observed on RoseMcp.Tray, an unpackaged WinUI 3 app. Note both Microsoft names.</summary>
	private static readonly string[] WinUi3 =
	[
		"ntdll.dll",
		"Microsoft.InteractiveExperiences.Projection.dll",
		"Microsoft.UI.Xaml.Controls.dll",
		"Microsoft.UI.Xaml.dll",
		"Microsoft.WindowsAppRuntime.Bootstrap.dll",
		"Microsoft.WinUI.dll",
	];

	/// <summary>
	/// Observed on a UWP app built for net10.0-windows with <c>UseUwp</c>, launched and read on
	/// 2026-09-05. The XAML framework is the same OS DLL classic UWP loads; what is new is the
	/// CsWinRT projection assembly beside it, and coreclr rather than .NET Native.
	/// </summary>
	private static readonly string[] ModernUwp =
	[
		"ntdll.dll",
		"coreclr.dll",
		"hostpolicy.dll",
		"Microsoft.Windows.UI.Xaml.dll",
		"Windows.UI.Xaml.dll",
	];

	/// <summary>
	/// UWP on modern .NET is UWP, and the interesting part is the module that nearly says otherwise.
	/// A <c>UseUwp</c> app loads <c>Microsoft.Windows.UI.Xaml.dll</c> -- the CsWinRT projection, a
	/// managed assembly -- beside the framework's own <c>Windows.UI.Xaml.dll</c>. It is a third name
	/// in a family of three, it is the only one of them that is not a XAML framework, and it reads at
	/// a glance like the WinUI signal.
	/// </summary>
	[Test]
	public void Recognises_uwp_on_modern_dotnet()
	{
		var (stack, evidence) = XamlStackModules.Identify(ModernUwp);

		Assert.Equal(XamlStack.Uwp, stack);
		Assert.Equal(["Windows.UI.Xaml.dll"], evidence);
	}

	/// <summary>
	/// The projection assembly on its own decides nothing, and this is the test that stops the
	/// matcher being loosened. Matching is by whole name: <c>Microsoft.Windows.UI.Xaml.dll</c> is not
	/// <c>Microsoft.UI.Xaml.dll</c>, so it contributes to no verdict at all. Relax that to a
	/// substring or a prefix on <c>Microsoft.*.Xaml</c> and a modern UWP app becomes a WinUI 3 one --
	/// which sends the injection at the wrong endpoint with the wrong initialiser, and the WinUI tap
	/// then waits twenty seconds for a framework that is not there.
	/// </summary>
	[Test]
	public void The_uwp_projection_assembly_is_not_the_winui_signal()
	{
		var (stack, evidence) = XamlStackModules.Identify(["ntdll.dll", "Microsoft.Windows.UI.Xaml.dll"]);

		Assert.Equal(XamlStack.Unknown, stack);
		Assert.Empty(evidence);
	}

	[Test]
	public void Recognises_classic_uwp()
	{
		var (stack, evidence) = XamlStackModules.Identify(ClassicUwp);

		Assert.Equal(XamlStack.Uwp, stack);
		Assert.Equal(["Windows.UI.Xaml.dll"], evidence);
	}

	[Test]
	public void Recognises_winui3()
	{
		var (stack, evidence) = XamlStackModules.Identify(WinUi3);

		Assert.Equal(XamlStack.WinUi, stack);
		Assert.Contains("Microsoft.WinUI.dll", evidence);
	}

	/// <summary>
	/// The one that decides the ordering. A UWP app using WinUI 2 loads Microsoft.UI.Xaml.dll beside
	/// Windows.UI.Xaml.dll, and it is a UWP app: its XAML framework is Windows.UI.Xaml and that is
	/// where InitializeXamlDiagnosticsEx lives. Matching the Microsoft name first would report WinUI 3
	/// and refuse to serve a target the UWP tap handles perfectly well -- a confident wrong answer,
	/// which is the failure shape this repository has already paid for twice in source classification.
	/// </summary>
	[Test]
	public void A_uwp_app_hosting_winui2_is_uwp_not_winui3()
	{
		string[] modules = ["Windows.UI.Xaml.dll", "Microsoft.UI.Xaml.dll", "Microsoft.UI.Xaml.Controls.dll"];

		var (stack, evidence) = XamlStackModules.Identify(modules);

		Assert.Equal(XamlStack.Uwp, stack);
		Assert.Equal(["Windows.UI.Xaml.dll"], evidence);
	}

	[Test]
	public void Recognises_wpf()
	{
		var (stack, evidence) = XamlStackModules.Identify(["clr.dll", "PresentationFramework.dll", "PresentationCore.dll"]);

		Assert.Equal(XamlStack.Wpf, stack);
		Assert.Equal(["PresentationFramework.dll"], evidence);
	}

	/// <summary>
	/// Unknown carries no evidence, because there is none: naming the modules that did not match
	/// would read as a finding about them.
	/// </summary>
	[Test]
	public void A_process_with_no_xaml_framework_is_unknown_and_cites_nothing()
	{
		var (stack, evidence) = XamlStackModules.Identify(["ntdll.dll", "kernel32.dll", "coreclr.dll"]);

		Assert.Equal(XamlStack.Unknown, stack);
		Assert.Empty(evidence);
	}

	/// <summary>Module names come off the OS with whatever casing it used; matching cannot depend on it.</summary>
	[Test]
	public void Matching_ignores_case()
	{
		var (stack, _) = XamlStackModules.Identify(["WINDOWS.UI.XAML.DLL"]);

		Assert.Equal(XamlStack.Uwp, stack);
	}

	/// <summary>
	/// An empty list is Unknown rather than anything else. The probe separates "could not read the
	/// modules" from "read them and recognised nothing" in its own message; both arrive here the same
	/// way and neither is an occasion to guess.
	/// </summary>
	[Test]
	public void No_modules_is_unknown()
	{
		var (stack, evidence) = XamlStackModules.Identify([]);

		Assert.Equal(XamlStack.Unknown, stack);
		Assert.Empty(evidence);
	}
}
