namespace RoseMcp.Worker.Xaml;

/// <summary>
/// UWP XAML, on .NET Core 2.x through to UseUwp on .NET 10. Both project shapes resolve the same
/// Windows.UI.Xaml types, so one dialect serves them.
/// </summary>
public sealed class UwpXamlDialect : IXamlDialect
{
	public string Name => "UWP";

	public string MarkerTypeName => "Windows.UI.Xaml.Controls.Control";

	public string UsingNamespaceRoot => "Windows.UI.Xaml";

	/// <summary>
	/// Controls first because most elements are controls, then primitives, then the root namespace
	/// which holds VisualState, VisualStateGroup and the element base types. Taken from what the
	/// real generator produced for this solution rather than from guesswork.
	/// </summary>
	public IReadOnlyList<string> FrameworkNamespaces =>
	[
		"Windows.UI.Xaml.Controls",
		"Windows.UI.Xaml.Controls.Primitives",
		"Windows.UI.Xaml",
		"Windows.UI.Xaml.Shapes",
		"Windows.UI.Xaml.Documents",
		"Windows.UI.Xaml.Media",
		"Windows.UI.Xaml.Media.Animation",
		"Windows.UI.Xaml.Media.Imaging",
		"Windows.UI.Xaml.Data",
		"Windows.UI.Xaml.Input",
		"Windows.UI.Xaml.Automation",
	];

	public IEnumerable<string> ExtraMembers(XamlDocument document)
	{
		yield return "private bool _contentLoaded;";

		// A declaration, not an implementation: code-behind that writes `partial void UnloadObject`
		// is otherwise CS0759, with no defining declaration to attach to. There were 62 of those.
		yield return "partial void UnloadObject(global::Windows.UI.Xaml.DependencyObject unloadableObject);";

		if (!document.UsesCompiledBindings) yield break;

		// Shaped as the real generator shapes it, down to the interface name, so hand-written code
		// that names the type keeps working. Only the members code-behind plausibly calls are here.
		yield return $"private interface I{document.TypeName}_Bindings";
		yield return "{";
		yield return "\tvoid Initialize();";
		yield return "\tvoid Update();";
		yield return "\tvoid StopTracking();";
		yield return "}";
		yield return $"private I{document.TypeName}_Bindings Bindings;";
	}
}
