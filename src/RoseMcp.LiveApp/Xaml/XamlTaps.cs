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
		EndpointName = "VisualDiagConnection1",
		InitializeLibrary = "Windows.UI.Xaml.dll",

		// Null: the initialiser is exported from the framework itself, so it needs no telling which
		// framework it serves.
		DiagnosticsModule = null,

		ProviderFileName = "RoseMcp.Xaml.Uwp.Tap.dll",

		// Must match CLSID_RoseTap in src/RoseMcp.Xaml.Uwp.Tap/RoseMcp.Xaml.Uwp.Tap.cpp.
		ProviderClsid = new("7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6"),
		NeedsAppContainerGrants = true,
		ProviderProjectName = "RoseMcp.Xaml.Uwp.Tap",
	};

	private static readonly XamlTap WinUiTap = new()
	{
		Stack = XamlStack.WinUi,

		// Not VisualDiagConnection1. The framework builds this name at runtime as prefix + instance
		// count (DXamlCore.cpp), so the binary's string table holds only "WinUIVisualDiagConnection"
		// and reading it there suggests, wrongly, that there is no suffix. The first core in a process
		// is 1, and every sample in microsoft-ui-xaml uses that.
		EndpointName = "WinUIVisualDiagConnection1",

		// Not Microsoft.UI.Xaml.dll, which is the obvious guess and is wrong: it exports eight
		// functions and InitializeXamlDiagnosticsEx is not among them. The initialiser lives in the
		// WindowsAppRuntime framework package's FrameworkUdk, which is on no search path here, so the
		// name is resolved against the target's own loaded modules rather than loaded bare.
		InitializeLibrary = "Microsoft.Internal.FrameworkUdk.dll",

		// The FrameworkUdk serves a framework rather than being one, so it has to be told which XAML
		// dll to attach diagnostics to. Resolved from the target, because a machine can have several
		// WindowsAppRuntime versions installed and only the target knows which it is running.
		DiagnosticsModule = "Microsoft.UI.Xaml.dll",

		ProviderFileName = "RoseMcp.Xaml.WinUi.Tap.dll",

		// Must match CLSID_RoseTap in src/RoseMcp.Xaml.WinUi.Tap/RoseMcp.Xaml.WinUi.Tap.cpp. Its own,
		// and it has to be: both providers may be staged on one machine at once.
		ProviderClsid = new("2af9a655-49a7-43ee-b6fc-ff579688d311"),

		// False for both shapes, and measured rather than reasoned about. A packaged WinUI 3 app is a
		// packaged *desktop* app -- runFullTrust, Windows.FullTrustApplication -- so it has package
		// identity and is not in an AppContainer; an unpackaged one has neither. Only classic UWP has
		// both. Granting anyway would leave a world-readable directory in TEMP for every session, for
		// a target that can read the folder perfectly well without it.
		NeedsAppContainerGrants = false,
		ProviderProjectName = "RoseMcp.Xaml.WinUi.Tap",
	};

	/// <summary>The tap serving this stack, or null when none does.</summary>
	public static XamlTap? For(XamlStack stack) => stack switch
	{
		XamlStack.Uwp => UwpTap,
		XamlStack.WinUi => WinUiTap,
		_ => null,
	};

	/// <summary>
	/// Why a stack has no tap, as a sentence for the caller. Kept beside the table because the table
	/// is what knows, and said in full because the cases are not the same kind of answer: one is a
	/// different mechanism entirely, and one is not knowing what we are looking at.
	/// </summary>
	public static string NoTapReason(XamlStack stack) => stack switch
	{
		XamlStack.Wpf =>
			"this is a WPF app. WPF's live diagnostics are a managed mechanism "
				+ "(System.Windows.Diagnostics.VisualDiagnostics) rather than an injected COM tap, so this "
				+ "path does not serve it.",

		_ =>
			"the XAML framework could not be determined from the target's loaded modules, so there is no "
				+ "provider to inject.",
	};
}
