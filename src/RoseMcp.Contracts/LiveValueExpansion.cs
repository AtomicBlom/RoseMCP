namespace RoseMcp.Contracts;

/// <summary>
/// One value's own children: an object's fields, or an array's elements.
/// <para>
/// Expanded on demand rather than read with the frame, because an object graph has no bound and
/// reading one eagerly would make every stop pay for a tree nobody opened. Each child carries the
/// <see cref="LiveVariable.Path"/> that expands it in turn, so a reader walks the graph without
/// composing addresses itself.
/// </para>
/// <para>
/// Nothing here runs the debuggee's code. Fields are read from memory, so a property with a getter
/// is not among the children -- what is listed is what the object actually holds.
/// </para>
/// </summary>
public sealed record LiveValueExpansion : LiveExecutionReport
{
	/// <summary>The path that was expanded, echoed so an answer can be matched to its request.</summary>
	public required string Path { get; init; }

	/// <summary>The type of the value at that path.</summary>
	public string? TypeName { get; init; }

	/// <summary>The value itself, rendered the way a frame's variables are.</summary>
	public string? Value { get; init; }

	public IReadOnlyList<LiveVariable> Children { get; init; } = [];

	/// <summary>How many children the value has, which may exceed the number reported.</summary>
	public required int Total { get; init; }

	/// <summary>Whether the children were cut short at the reporting limit.</summary>
	public required bool Truncated { get; init; }
}
