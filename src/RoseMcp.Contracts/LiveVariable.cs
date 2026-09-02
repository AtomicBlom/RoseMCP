namespace RoseMcp.Contracts;

/// <summary>
/// One argument or local read from a stopped frame. Primitives and strings carry a real
/// <see cref="Value"/>; an object shows its type and is rendered as <c>{TypeName}</c>, because reading
/// an object's own value (its ToString) needs func-eval, which is a later slice. Local names require a
/// PDB and are indexed (<c>local_0</c>) when one is not available; argument names come from metadata.
/// </summary>
public sealed record LiveVariable
{
	public required string Name { get; init; }

	/// <summary>"argument" or "local".</summary>
	public required string Kind { get; init; }

	public string? TypeName { get; init; }

	public string? Value { get; init; }
}
