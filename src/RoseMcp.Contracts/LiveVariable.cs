namespace RoseMcp.Contracts;

/// <summary>
/// One argument or local read from a stopped frame. Primitives and strings carry a real
/// <see cref="Value"/>; an object shows its type and is rendered as <c>{TypeName}</c>, because reading
/// an object's own value -- its ToString -- means running the debuggee's code, which this deliberately
/// never does.
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

	/// <summary>"argument" or "local".</summary>
	public required string Kind { get; init; }

	public string? TypeName { get; init; }

	public string? Value { get; init; }
}
