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
	/// Accessibility a named field gets when the markup does not say. WPF generates internal fields
	/// and the Windows frameworks generate private ones, and the difference is load-bearing: too
	/// narrow and a reference from another class becomes CS0122 rather than merely missing.
	/// </summary>
	string DefaultFieldModifier { get; }

	/// <summary>
	/// Namespaces searched in order for an element written in the presentation namespace. Order is
	/// the tie-break between framework types sharing a name, mirroring what the real compiler does
	/// with its own precedence.
	/// </summary>
	IReadOnlyList<string> FrameworkNamespaces { get; }

	/// <summary>
	/// Whether a field for the named root element is typed as the generated class rather than as the
	/// element the markup writes.
	/// <para>
	/// The two answers were read off real generated files, and the frameworks disagree. WPF writes
	/// <c>internal ProjectSelectionView ProjectSelectionRoot</c> for a root named in markup; UWP
	/// writes <c>private Page CanvasWorkspace</c> for the same shape. Getting WPF's wrong is not
	/// cosmetic: the class is a subtype of the element, so typing the field as the element makes
	/// every member of the view itself unreachable through it.
	/// </para>
	/// </summary>
	bool RootFieldIsTheClass { get; }

	/// <summary>
	/// Members the real generator emits beyond the base type and the named fields, as source. Kept
	/// on the dialect so the emitter needs no idea which framework it is serving.
	/// </summary>
	IEnumerable<string> ExtraMembers(XamlDocument document);
}
