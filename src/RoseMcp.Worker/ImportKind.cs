namespace RoseMcp.Worker;

/// <summary>
/// What a using directive imports. Declared in the order a file lists them, which is the order the
/// IDE's own sort produces: namespaces, then static imports, then aliases.
/// </summary>
public enum ImportKind
{
	/// <summary><c>using System.Text;</c></summary>
	Namespace,

	/// <summary><c>using static System.Math;</c></summary>
	Static,

	/// <summary><c>using Json = System.Text.Json;</c></summary>
	Alias,
}
