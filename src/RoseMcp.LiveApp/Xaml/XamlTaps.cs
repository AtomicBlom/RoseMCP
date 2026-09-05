using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The taps that exist, by stack. One table, so adding WinUI 3 (#76) is a row rather than a hunt
/// through the session for the places that assumed UWP -- there were four.
/// </summary>
internal static class XamlTaps
{
	private static readonly XamlTap UwpTap = new()
	{
		Stack = XamlStack.Uwp,
		InitializeLibrary = "Windows.UI.Xaml.dll",
		ProviderFileName = "RoseMcp.Xaml.Uwp.Tap.dll",

		// Must match CLSID_RoseTap in src/RoseMcp.Xaml.Uwp.Tap/RoseMcp.Xaml.Uwp.Tap.cpp.
		ProviderClsid = new("7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6"),
		NeedsAppContainerGrants = true,
		ProviderProjectName = "RoseMcp.Xaml.Uwp.Tap",
	};

	/// <summary>The tap serving this stack, or null when none does.</summary>
	public static XamlTap? For(XamlStack stack) => stack switch
	{
		XamlStack.Uwp => UwpTap,
		_ => null,
	};

	/// <summary>
	/// Why a stack has no tap, as a sentence for the caller. Kept beside the table because the table
	/// is what knows, and said in full because the three cases are not the same kind of answer: one
	/// is unbuilt, one is a different mechanism entirely, and one is not knowing what we are looking
	/// at.
	/// </summary>
	public static string NoTapReason(XamlStack stack) => stack switch
	{
		XamlStack.WinUi =>
			"this is a WinUI 3 app and no WinUI 3 provider has been built yet. The tap's shared half is "
				+ "in src/RoseMcp.Xaml.Tap and only the UWP binding exists so far.",

		XamlStack.Wpf =>
			"this is a WPF app. WPF's live diagnostics are a managed mechanism "
				+ "(System.Windows.Diagnostics.VisualDiagnostics) rather than an injected COM tap, so this "
				+ "path does not serve it.",

		_ =>
			"the XAML framework could not be determined from the target's loaded modules, so there is no "
				+ "provider to inject.",
	};
}
