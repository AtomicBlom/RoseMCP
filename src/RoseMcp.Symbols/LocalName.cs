namespace RoseMcp.Symbols;

/// <summary>
/// One local variable's name and the slot it occupies in the method's locals signature, which is how
/// a debugger addresses it.
/// </summary>
public sealed record LocalName
{
	/// <summary>The slot, which is the index a debugger enumerates locals by.</summary>
	public required int Slot { get; init; }

	public required string Name { get; init; }
}

/// <summary>
/// A lexical scope out of a method's debug information: the IL it covers and the locals declared in
/// it.
/// <para>
/// Scopes nest, and that is the whole reason they are exposed rather than flattened. A local
/// declared inside an <c>if</c> exists only for that block's IL, and its slot may be reused by a
/// different local elsewhere in the method -- so naming every slot from every scope at once would
/// hand back two names for one slot and pick the wrong one roughly half the time.
/// </para>
/// </summary>
public sealed record LocalScopeInfo
{
	public required int StartOffset { get; init; }

	public required int Length { get; init; }

	public required IReadOnlyList<LocalName> Locals { get; init; }

	/// <summary>Whether this scope covers an IL offset.</summary>
	public bool Covers(int ilOffset) => ilOffset >= StartOffset && ilOffset < StartOffset + Length;
}
