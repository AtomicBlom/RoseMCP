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

	[Fact]
	public void Recognises_classic_uwp()
	{
		var (stack, evidence) = XamlStackModules.Identify(ClassicUwp);

		Assert.Equal(XamlStack.Uwp, stack);
		Assert.Equal(["Windows.UI.Xaml.dll"], evidence);
	}

	[Fact]
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
	[Fact]
	public void A_uwp_app_hosting_winui2_is_uwp_not_winui3()
	{
		string[] modules = ["Windows.UI.Xaml.dll", "Microsoft.UI.Xaml.dll", "Microsoft.UI.Xaml.Controls.dll"];

		var (stack, evidence) = XamlStackModules.Identify(modules);

		Assert.Equal(XamlStack.Uwp, stack);
		Assert.Equal(["Windows.UI.Xaml.dll"], evidence);
	}

	[Fact]
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
	[Fact]
	public void A_process_with_no_xaml_framework_is_unknown_and_cites_nothing()
	{
		var (stack, evidence) = XamlStackModules.Identify(["ntdll.dll", "kernel32.dll", "coreclr.dll"]);

		Assert.Equal(XamlStack.Unknown, stack);
		Assert.Empty(evidence);
	}

	/// <summary>Module names come off the OS with whatever casing it used; matching cannot depend on it.</summary>
	[Fact]
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
	[Fact]
	public void No_modules_is_unknown()
	{
		var (stack, evidence) = XamlStackModules.Identify([]);

		Assert.Equal(XamlStack.Unknown, stack);
		Assert.Empty(evidence);
	}
}
