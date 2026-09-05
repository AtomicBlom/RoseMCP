using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// One injected XAML diagnostics provider: which framework DLL exports the initialiser, which
/// provider binary to stage, the class id it registers, and whether the folder they exchange files
/// through needs AppContainer grants.
/// <para>
/// Named a tap rather than a provider because injection is one mechanism, not the concept (#74).
/// WPF's live diagnostics are managed -- <c>System.Windows.Diagnostics.VisualDiagnostics</c>, gated
/// on <c>ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO</c> -- so a WPF provider would report through the same
/// channel without any of the fields below meaning anything. A stack having no tap is therefore a
/// normal answer, and for WPF it is the permanent one.
/// </para>
/// </summary>
internal sealed record XamlTap
{
	public required XamlStack Stack { get; init; }

	/// <summary>
	/// The well-known diagnostics endpoint to ask for. Anything else makes
	/// <c>InitializeXamlDiagnosticsEx</c> return ERROR_NOT_FOUND (0x80070490).
	/// <para>
	/// Per-stack, which was found the hard way and is not documented anywhere I could find: UWP's is
	/// <c>VisualDiagConnection1</c> and WinUI 3's is <c>WinUIVisualDiagConnection</c>, with no numeric
	/// suffix. Injecting into a WinUI 3 app with UWP's name binds, runs, and then waits twenty seconds
	/// for an endpoint that will never appear -- which is indistinguishable from an app that has no
	/// XAML in it. The name was read out of Microsoft.UI.Xaml.dll's own string table, which is the
	/// only place it appears.
	/// </para>
	/// </summary>
	public required string EndpointName { get; init; }

	/// <summary>
	/// The framework DLL exporting <c>InitializeXamlDiagnosticsEx</c>. Loaded by name at the call
	/// site rather than bound by a <c>DllImport</c> attribute, because the attribute can only name
	/// one and this is exactly the thing that differs per stack.
	/// </summary>
	public required string InitializeLibrary { get; init; }

	/// <summary>
	/// The XAML framework module the initialiser should be pointed at, or null to let it find its
	/// own.
	/// <para>
	/// UWP leaves this null: <c>InitializeXamlDiagnosticsEx</c> is exported from Windows.UI.Xaml.dll
	/// itself, so the thing being asked already knows which framework it is talking about. WinUI 3
	/// splits them -- the export lives in the WindowsAppRuntime's FrameworkUdk, which serves a
	/// framework it has to be told about -- and the module is resolved from the target, so the path
	/// is the copy that target is actually running.
	/// </para>
	/// </summary>
	public required string? DiagnosticsModule { get; init; }

	public required string ProviderFileName { get; init; }

	public required Guid ProviderClsid { get; init; }

	/// <summary>
	/// Whether the work folder needs granting to ALL APPLICATION PACKAGES. True for UWP, which runs
	/// in an AppContainer that can otherwise touch none of it. An unpackaged WinUI 3 app is not in
	/// an AppContainer and needs nothing, which is why this is a field and not a constant: granting
	/// where it is not needed leaves world-readable directories behind for no reason.
	/// </summary>
	public required bool NeedsAppContainerGrants { get; init; }

	/// <summary>The provider's project directory under <c>src</c>, named when the binary is missing.</summary>
	public required string ProviderProjectName { get; init; }
}
