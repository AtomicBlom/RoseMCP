namespace RoseMcp.Worker.Xaml;

/// <summary>
/// One XAML flavour: where its framework types live, and what its generated partial carries beyond
/// the base type and the named fields.
/// <para>
/// WPF, UWP and WinUI write markup that is indistinguishable at the namespace level -- all three use
/// the same presentation URI -- so what separates them is which assemblies a project references and
/// which namespaces those element names resolve in. That is exactly what a dialect describes.
/// </para>
/// </summary>
public interface IXamlDialect
{
	/// <summary>Short name, reported in workspace status so a wrong guess is visible.</summary>
	string Name { get; }

	/// <summary>
	/// A type that exists only when this framework is referenced. Resolving it against the project's
	/// compilation is the primary and usually sufficient test.
	/// </summary>
	string MarkerTypeName { get; }

	/// <summary>
	/// Namespace root used to recognise this dialect in the code-behind's using directives, for the
	/// case where a migration project references two frameworks at once.
	/// </summary>
	string UsingNamespaceRoot { get; }

	/// <summary>
	/// Namespaces searched in order for an element written in the presentation namespace. Order is
	/// the tie-break between framework types sharing a name, mirroring what the real compiler does
	/// with its own precedence.
	/// </summary>
	IReadOnlyList<string> FrameworkNamespaces { get; }

	/// <summary>
	/// Members the real generator emits beyond the base type and the named fields, as source. Kept
	/// on the dialect so the emitter needs no idea which framework it is serving.
	/// </summary>
	IEnumerable<string> ExtraMembers(XamlDocument document);
}
