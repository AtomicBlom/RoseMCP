namespace RoseMcp.Contracts;

/// <summary>
/// One argument or local read from a stopped frame. Primitives and strings carry a real
/// <see cref="Value"/>; an object shows its type and is rendered as <c>{TypeName}</c>, because reading
/// an object's own value -- its ToString -- means running the debuggee's code, which this deliberately
/// never does. Argument names come from metadata; locals are numbered by slot, <c>local_0</c> upwards,
/// whether or not a PDB is there to name them (#83).
/// </summary>
public sealed record LiveVariable
{
	public required string Name { get; init; }

	/// <summary>"argument" or "local".</summary>
	public required string Kind { get; init; }

	public string? TypeName { get; init; }

	public string? Value { get; init; }
}
