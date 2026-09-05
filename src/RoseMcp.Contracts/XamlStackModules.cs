namespace RoseMcp.Contracts;

/// <summary>
/// Which loaded modules mean which <see cref="XamlStack"/>, and the rule for reading them.
/// <para>
/// Beside the enum rather than in the live-app host for two reasons. The module names are what give
/// the enum meaning to anything looking at a running process, so they are the same fact. And the
/// ordering below is subtle enough to need a regression test, while the host is net10.0-windows and
/// neither test project takes a compile reference on it -- so a rule left there could only have been
/// covered by launching real apps of three frameworks.
/// </para>
/// <para>
/// Every name here was read off a running process rather than reasoned about, which is the rule #52
/// sets after source classification was wrong for two stacks running.
/// </para>
/// </summary>
public static class XamlStackModules
{
	/// <summary>
	/// Classic UWP and UWP on modern .NET. Observed alongside Windows.UI.Xaml.Controls.dll and
	/// System.Runtime.WindowsRuntime.UI.Xaml.dll, with no Microsoft.* XAML module present.
	/// </summary>
	public static IReadOnlyList<string> Uwp { get; } = ["Windows.UI.Xaml.dll"];

	/// <summary>
	/// WinUI 3. Microsoft.WinUI.dll is the discriminating name; Microsoft.UI.Xaml.dll is loaded too
	/// but is also what WinUI 2 ships into a UWP app, so it cannot decide anything on its own.
	/// </summary>
	public static IReadOnlyList<string> WinUi { get; } = ["Microsoft.WinUI.dll", "Microsoft.UI.Xaml.dll"];

	/// <summary>WPF, whose live diagnostics are a managed mechanism rather than an injected tap.</summary>
	public static IReadOnlyList<string> Wpf { get; } = ["PresentationFramework.dll"];

	/// <summary>
	/// The stack these loaded modules indicate, with the ones that decided it.
	/// <para>
	/// The order is the whole subtlety, and it is the reason this is not the rule the issue proposed.
	/// <c>Windows.UI.Xaml.dll</c> is tested first because it is the only unambiguous signal: a WinUI 3
	/// process never loads it, whereas a UWP app hosting WinUI 2 loads <c>Microsoft.UI.Xaml.dll</c>
	/// beside it -- and that app is UWP. Matching the Microsoft names first would call it WinUI 3 and
	/// send an injection at a framework that is not there, which is the same shape of confident wrong
	/// answer that #43 and the WinUI 2 <c>ms-appx</c> escape already cost this repository twice.
	/// </para>
	/// </summary>
	public static (XamlStack Stack, IReadOnlyList<string> Evidence) Identify(IReadOnlyList<string> loadedModules)
	{
		if (Matching(loadedModules, Uwp) is { Count: > 0 } uwp) return (XamlStack.Uwp, uwp);
		if (Matching(loadedModules, WinUi) is { Count: > 0 } winui) return (XamlStack.WinUi, winui);
		if (Matching(loadedModules, Wpf) is { Count: > 0 } wpf) return (XamlStack.Wpf, wpf);

		return (XamlStack.Unknown, []);
	}

	private static IReadOnlyList<string> Matching(IReadOnlyList<string> loadedModules, IReadOnlyList<string> candidates) =>
		[.. candidates.Where(candidate =>
			loadedModules.Any(module => module.Equals(candidate, StringComparison.OrdinalIgnoreCase)))];
}
