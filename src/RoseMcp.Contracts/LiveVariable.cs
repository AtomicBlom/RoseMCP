namespace RoseMcp.Contracts;

/// <summary>
/// One argument, local, field or array element read from a stopped frame. Primitives and strings
/// carry a real <see cref="Value"/>; an object shows its type and is rendered as <c>{TypeName}</c>,
/// because reading an object's own value -- its ToString -- means running the debuggee's code, which
/// this deliberately never does.
/// <para>
/// Argument names come from metadata. Local names come from the module's portable PDB, which is why
/// they are the source names the code declares rather than slot numbers; a module built without
/// symbols, or one whose PDB belongs to a different build, leaves them as <c>local_0</c> upwards by
/// slot. A compiler-generated local has no name in the PDB at all and keeps its slot name too, which
/// is the honest answer rather than borrowing the name next to it.
/// </para>
/// </summary>
public sealed record LiveVariable
{
	public required string Name { get; init; }

	/// <summary>"argument", "local", "field" or "element".</summary>
	public required string Kind { get; init; }

	public string? TypeName { get; init; }

	public string? Value { get; init; }

	/// <summary>
	/// How to address this value again: <c>arg:0</c>, <c>local:2</c>, then <c>.field</c> and
	/// <c>[3]</c> for anything reached by expanding it.
	/// <para>
	/// Carried rather than composed by the reader, because the two ends have to agree exactly and
	/// only one of them knows how the name was arrived at. A local named from a PDB and one named by
	/// its slot are addressed identically, which is what lets a caller expand a value it was shown
	/// without knowing whether symbols were there.
	/// </para>
	/// </summary>
	public required string Path { get; init; }

	/// <summary>
	/// Whether expanding this value would yield anything: a non-null object with fields, a non-empty
	/// array, or a boxed value. It is what lets a tree show an expander only where there is
	/// something behind it, without a read per row.
	/// </summary>
	public required bool HasChildren { get; init; }
}
