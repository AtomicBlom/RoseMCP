namespace RoseMcp.Contracts;

/// <summary>
/// What a type or a file declares, as a list rather than as text to read.
/// <para>
/// The read that comes before an edit and had no tool: what does this type contain, what is in this
/// file, what would I be implementing. Answering it with a file read is what puts the file in front
/// of the caller, and the next edit then goes through a text tool.
/// </para>
/// </summary>
public sealed record OutlineResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>What was asked about: a type name or a file path, as the caller wrote it.</summary>
	public required string Target { get; init; }

	/// <summary>The types found, outermost first, each with its members.</summary>
	public required IReadOnlyList<OutlinedType> Types { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}

/// <summary>One type and what it declares.</summary>
public sealed record OutlinedType
{
	public required string Name { get; init; }

	/// <summary>class, interface, struct, record, enum, or delegate.</summary>
	public required string Kind { get; init; }

	public required string Accessibility { get; init; }

	public string? Namespace { get; init; }

	/// <summary>Base class and interfaces, so implementing one does not need a second call.</summary>
	public IReadOnlyList<string> BaseTypes { get; init; } = [];

	/// <summary>The first sentence of its documentation, where it has one.</summary>
	public string? Summary { get; init; }

	/// <summary>Every file declaring it, which is more than one for a partial.</summary>
	public IReadOnlyList<SourceLocation> Declarations { get; init; } = [];

	public required IReadOnlyList<OutlinedMember> Members { get; init; }
}

/// <summary>One member of a type: enough to decide about it without reading the file.</summary>
public sealed record OutlinedMember
{
	public required string Name { get; init; }

	/// <summary>The full signature, so an implementer can be written from this alone.</summary>
	public required string Signature { get; init; }

	public required string Kind { get; init; }

	public required string Accessibility { get; init; }

	/// <summary>True where the member is abstract, which is what has to be implemented.</summary>
	public bool IsAbstract { get; init; }

	public bool IsStatic { get; init; }

	/// <summary>
	/// True where the member is written by a generator rather than by a file, so an attempt to edit
	/// it would be an attempt to edit something that is not there.
	/// </summary>
	public bool IsGenerated { get; init; }

	public string? Summary { get; init; }

	/// <summary>Where it is, so the next call can name the file without a search.</summary>
	public SourceLocation? Location { get; init; }
}
