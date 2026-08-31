namespace RoseMcp.Worker.Xaml;

/// <summary>
/// An element name as XAML writes it: the XML namespace it came from, and the local name.
/// <para>
/// The namespace is kept as the raw URI rather than resolved to a prefix, because that is what
/// carries the meaning. <c>using:Drawboard.Controls</c> and <c>clr-namespace:Foo;assembly=Bar</c>
/// name a CLR namespace outright, while the presentation URI means "look in whichever framework
/// this project actually references" -- which is the dialect's job to answer.
/// </para>
/// </summary>
public readonly record struct XamlTypeName(string NamespaceUri, string LocalName)
{
	/// <summary>Shared verbatim by WPF, UWP and WinUI, so it identifies a framework type and nothing more.</summary>
	public const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	/// <summary>The XAML language namespace, where x:Class and x:Name live.</summary>
	public const string LanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

	private const string UsingPrefix = "using:";
	private const string ClrNamespacePrefix = "clr-namespace:";

	/// <summary>True when the element is a framework type whose namespace only the dialect knows.</summary>
	public bool IsFrameworkType => NamespaceUri is PresentationNamespace or "";

	/// <summary>
	/// The CLR namespace this element names outright, or null when only the dialect can say.
	/// <para>
	/// UWP and WinUI write <c>using:Some.Namespace</c>; WPF writes
	/// <c>clr-namespace:Some.Namespace;assembly=Some.Assembly</c>. The assembly half is dropped: the
	/// type is looked up across everything the project references, which finds it wherever it lives
	/// and does not care whether the markup named the assembly correctly.
	/// </para>
	/// </summary>
	public string? ClrNamespace
	{
		get
		{
			if (NamespaceUri.StartsWith(UsingPrefix, StringComparison.Ordinal))
			{
				return NamespaceUri[UsingPrefix.Length..].Trim();
			}

			if (!NamespaceUri.StartsWith(ClrNamespacePrefix, StringComparison.Ordinal)) return null;

			var declaration = NamespaceUri[ClrNamespacePrefix.Length..];
			var semicolon = declaration.IndexOf(';', StringComparison.Ordinal);

			return (semicolon < 0 ? declaration : declaration[..semicolon]).Trim();
		}
	}
}
