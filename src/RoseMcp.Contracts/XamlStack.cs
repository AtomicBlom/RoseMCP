namespace RoseMcp.Contracts;

/// <summary>
/// Which XAML framework is in play. One name for a question this repository asks twice, from two
/// places that can reach two different sources of truth.
/// <para>
/// The workspace half asks a compilation -- which marker type resolves, then a census of code-behind
/// using directives, then markup syntax. The live half cannot: it holds a process id for an app it
/// may never have built, so it asks the process which framework DLLs it has loaded. Neither is a
/// substitute for the other, and they are not required to agree: a solution can hold a WPF project
/// and a WinUI project at once, and the process in front of you settles which one you are looking at.
/// </para>
/// <para>
/// The value of naming it once is that the two answers become comparable. Before this, the live half
/// did not ask at all -- it assumed UWP in four places -- so a WinUI 3 target failed after a
/// twenty-second wait with a diagnosis about packaging, while the fact that settled it was sitting in
/// the target's loaded modules the whole time.
/// </para>
/// </summary>
public enum XamlStack
{
	/// <summary>Could not be determined, which is not the same as "no XAML".</summary>
	Unknown,

	/// <summary>Windows.UI.Xaml: classic UWP, and UWP on modern .NET.</summary>
	Uwp,

	/// <summary>Microsoft.UI.Xaml: WinUI 3, packaged or unpackaged.</summary>
	WinUi,

	/// <summary>PresentationFramework: WPF, whose live diagnostics are managed rather than a COM tap.</summary>
	Wpf,
}
