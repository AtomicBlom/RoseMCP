namespace RoseMcp.Worker.Xaml;

/// <summary>
/// UWP and WinUI XAML, which generate the same shape and differ only in where their types live.
/// <para>
/// One class for both because the difference really is just the root namespace: Windows.UI.Xaml
/// against Microsoft.UI.Xaml, with identical suffixes beneath. Two near-identical dialects would
/// have to be kept in step by hand.
/// </para>
/// <para>
/// WinUI is worth recognising even though its markup compiler does take part in design-time builds,
/// so its projects usually need no stub at all. Without the dialect, such a project reports "no XAML
/// framework is referenced" -- which is false, and misleading in a way silence would not be.
/// </para>
/// </summary>
public sealed class WindowsXamlDialect : IXamlDialect
{
	private readonly string _root;

	private WindowsXamlDialect(string name, string root)
	{
		Name = name;
		_root = root;
	}

	/// <summary>UWP, on .NET Core 2.x through to UseUwp on .NET 10. Both resolve the same types.</summary>
	public static WindowsXamlDialect Uwp { get; } = new("UWP", "Windows.UI.Xaml");

	public static WindowsXamlDialect WinUi { get; } = new("WinUI", "Microsoft.UI.Xaml");

	public string Name { get; }

	public string MarkerTypeName => $"{_root}.Controls.Control";

	public string UsingNamespaceRoot => _root;

	public string DefaultFieldModifier => "private";

	/// <summary>False: a named root is typed as the element, which is what the real files show.</summary>
	public bool RootFieldIsTheClass => false;

	/// <summary>
	/// Controls first because most elements are controls, then primitives, then the root namespace
	/// which holds VisualState, VisualStateGroup and the element base types. The order and the list
	/// come from what the real generator produced for a real solution, not from guesswork.
	/// </summary>
	public IReadOnlyList<string> FrameworkNamespaces =>
	[
		$"{_root}.Controls",
		$"{_root}.Controls.Primitives",
		_root,
		$"{_root}.Shapes",
		$"{_root}.Documents",
		$"{_root}.Media",
		$"{_root}.Media.Animation",
		$"{_root}.Media.Imaging",
		$"{_root}.Data",
		$"{_root}.Input",
		$"{_root}.Automation",
	];

	public IEnumerable<string> ExtraMembers(XamlDocument document)
	{
		yield return "private bool _contentLoaded;";

		// A declaration, not an implementation: code-behind that writes `partial void UnloadObject`
		// is otherwise CS0759, with no defining declaration to attach to. There were 62 of those.
		yield return $"partial void UnloadObject(global::{_root}.DependencyObject unloadableObject);";

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
