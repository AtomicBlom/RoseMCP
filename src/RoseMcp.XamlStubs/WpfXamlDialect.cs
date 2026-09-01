namespace RoseMcp.XamlStubs;

/// <summary>
/// WPF XAML, on .NET Framework and on modern .NET alike -- both run the same PresentationBuildTasks
/// markup compiler and generate the same shape, so one dialect serves net48 and net10 together.
/// <para>
/// Everything here was read off the .g.i.cs files a real build left in obj rather than reasoned
/// about, and two of the differences from the Windows frameworks would have been easy to get wrong.
/// Fields default to internal, not private, so a view reached from another class stays reachable.
/// And WPF declares the base type in the generated half exactly as UWP does, which the emitter's
/// existing rule about never contradicting a hand-written base list already handles.
/// </para>
/// <para>
/// Three things the real generator writes are deliberately left out, because nothing hand-written
/// refers to them and each one is a way to introduce an error of our own: the IComponentConnector
/// and IStyleConnector implementations, whose Connect methods only the loaded BAML calls; the
/// _CreateDelegate helper it reaches through reflection; and GeneratedInternalTypeHelper, which is
/// per-project rather than per-file and exists only to let the runtime touch internal types.
/// </para>
/// </summary>
public sealed class WpfXamlDialect : IXamlDialect
{
	private const string Root = "System.Windows";

	public static WpfXamlDialect Instance { get; } = new();

	public string Name => "WPF";

	/// <summary>
	/// From PresentationFramework, which is what an actual WPF project references. System.Windows
	/// itself is no test at all -- System.Windows.Input.ICommand ships in the base libraries, so that
	/// namespace exists in projects with no XAML anywhere near them.
	/// </summary>
	public string MarkerTypeName => $"{Root}.Controls.Control";

	public string UsingNamespaceRoot => Root;

	/// <summary>
	/// Internal, which is WPF's own default and not the private the Windows frameworks use. Measured:
	/// of 21 fields in one project's generated files, the three public ones all had an explicit
	/// x:FieldModifier and every other one was internal.
	/// </summary>
	public string DefaultFieldModifier => "internal";

	/// <summary>
	/// True, unlike the Windows frameworks. Checked both ways against real generated files rather
	/// than assumed to be shared.
	/// </summary>
	public bool RootFieldIsTheClass => true;

	/// <summary>
	/// The namespaces the markup compiler itself imports into a generated file, reordered by
	/// precedence rather than left in its alphabetical order. Controls first because most elements
	/// are controls, then primitives, then the root namespace which holds Window, Style and the
	/// element base types. This order reproduces every field type in the generated files it was
	/// checked against, Popup and RotateTransform and ContentControl included.
	/// </summary>
	public IReadOnlyList<string> FrameworkNamespaces =>
	[
		$"{Root}.Controls",
		$"{Root}.Controls.Primitives",
		Root,
		$"{Root}.Shapes",
		$"{Root}.Documents",
		$"{Root}.Media",
		$"{Root}.Media.Animation",
		$"{Root}.Media.Imaging",
		$"{Root}.Media.Effects",
		$"{Root}.Media.Media3D",
		$"{Root}.Data",
		$"{Root}.Input",
		$"{Root}.Navigation",
		$"{Root}.Shell",
		$"{Root}.Automation",
		$"{Root}.Controls.Ribbon",
		$"{Root}.Forms.Integration",
		$"{Root}.Ink",
		$"{Root}.Markup",
	];

	/// <summary>
	/// Only the flag, which is the one member of the generated half that hand-written WPF code is
	/// known to touch. There is no UnloadObject and no compiled-binding interface in WPF: {x:Bind}
	/// is a Windows-framework feature, and {Binding} generates nothing.
	/// </summary>
	public IEnumerable<string> ExtraMembers(XamlDocument document)
	{
		yield return "private bool _contentLoaded;";
	}
}
