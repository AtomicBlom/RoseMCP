namespace RoseMcp.XamlStubs;

/// <summary>
/// The parts of a XAML file that decide what its generated partial looks like. Everything else in
/// the markup -- layout, styles, bindings, resources -- is irrelevant to whether the code-behind
/// compiles, and is deliberately not read.
/// </summary>
public sealed record XamlDocument
{
	public required string Path { get; init; }

	/// <summary>
	/// Fully qualified name from x:Class, or null when the markup declares no code-behind. A file
	/// without it -- most resource dictionaries -- generates nothing.
	/// </summary>
	public required string? ClassName { get; init; }

	/// <summary>The root element, which is the base type the generated partial declares.</summary>
	public required XamlTypeName RootType { get; init; }

	public required IReadOnlyList<XamlNamedElement> NamedElements { get; init; }

	/// <summary>
	/// True when the markup uses {x:Bind}, which is what makes the generated Bindings member exist.
	/// Emitting it unconditionally would put a member on classes the real generator leaves alone.
	/// </summary>
	public required bool UsesCompiledBindings { get; init; }

	/// <summary>
	/// True when this is the App -- the root element being Application is what makes a file the
	/// application definition, and what makes the generated half responsible for the entry point.
	/// </summary>
	public bool IsApplicationDefinition => RootType.LocalName == "Application";

	/// <summary>The namespace half of <see cref="ClassName"/>, empty for a class in the global namespace.</summary>
	public string Namespace
	{
		get
		{
			var lastDot = ClassName?.LastIndexOf('.') ?? -1;
			return lastDot < 0 ? string.Empty : ClassName![..lastDot];
		}
	}

	/// <summary>The bare class name, without its namespace.</summary>
	public string TypeName
	{
		get
		{
			var lastDot = ClassName?.LastIndexOf('.') ?? -1;
			return lastDot < 0 ? ClassName ?? string.Empty : ClassName![(lastDot + 1)..];
		}
	}
}
