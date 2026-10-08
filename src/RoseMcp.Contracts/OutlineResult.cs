using System.Text.Json.Serialization;

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

	/// <summary>
	/// True where the outline stopped at the member cap before listing every member it found, so some
	/// type's <see cref="OutlinedType.TotalMembers"/> is larger than what it lists.
	/// </summary>
	public required bool Truncated { get; init; }

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

	/// <summary>Its documentation's summary, where the caller asked for documentation and it has one.</summary>
	public string? Summary { get; init; }

	/// <summary>
	/// The file its members are declared in, unless a member names another: the file asked about
	/// when the outline was of a file, and the type's first declaration when it was of a type.
	/// Said once here rather than on every member, where it would be most of what an outline costs.
	/// </summary>
	public string? FilePath { get; init; }

	/// <summary>
	/// Every file declaring it, which is more than one for a partial, each with the project compiling
	/// it and whether that is a test project.
	/// </summary>
	public IReadOnlyList<SourceLocation> Declarations { get; init; } = [];

	/// <summary>
	/// How many of its members the outline found: those whose name matched, where the caller filtered
	/// by name, and every one otherwise. More than <see cref="Members"/> holds when the outline stopped
	/// at its cap, which <see cref="OutlineResult.Truncated"/> says.
	/// </summary>
	public required int TotalMembers { get; init; }

	/// <summary>Its members in the order they are declared, inherited ones after its own.</summary>
	public required IReadOnlyList<OutlinedMember> Members { get; init; }
}

/// <summary>
/// One member of a type: enough to choose which members to look at, which is the whole job of an
/// outline. Everything that is the same for every member of the type -- the file, the project,
/// whether it is a test project -- is said once, on <see cref="OutlinedType"/>'s file path and
/// declarations, rather than on each member, and a flag a member does not have is left out rather
/// than written as false.
/// </summary>
public sealed record OutlinedMember
{
	public required string Name { get; init; }

	/// <summary>Method, Property, Field, Event, or NamedType for a nested type.</summary>
	public required string Kind { get; init; }

	/// <summary>
	/// The one-based line it is declared on, in <see cref="FilePath"/> where the member names one and
	/// in its type's <see cref="OutlinedType.FilePath"/> otherwise. Absent for a member with no source,
	/// which is an inherited one from a referenced assembly.
	/// </summary>
	public int? Line { get; init; }

	/// <summary>
	/// The file it is declared in, given only where that is not its type's file: another part of a
	/// partial type, a generator's output, or a base class's file for an inherited member.
	/// </summary>
	public string? FilePath { get; init; }

	public required string Accessibility { get; init; }

	/// <summary>
	/// The full signature, so an implementer can be written from this alone. Present only where the
	/// caller asked for signatures, because on a large type they are most of the answer and are not
	/// what choosing a member needs.
	/// </summary>
	public string? Signature { get; init; }

	/// <summary>True where the member is abstract, which is what has to be implemented. Absent when false.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsAbstract { get; init; }

	/// <summary>Absent when false.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsStatic { get; init; }

	/// <summary>
	/// True where the member is written by a generator rather than by a file, so an attempt to edit
	/// it would be an attempt to edit something that is not there. Absent when false.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsGenerated { get; init; }

	/// <summary>
	/// <c>warning</c> or <c>error</c> where the member is marked obsolete, which is what its
	/// <c>[Obsolete]</c> costs a caller: a warning fails a build that treats warnings as errors, and an
	/// error fails every build. Absent where it is not obsolete.
	/// </summary>
	public string? Obsolete { get; init; }

	/// <summary>
	/// The type that declares it, given only for an inherited member, which is the one case where the
	/// type the member is listed under is not the one it belongs to.
	/// </summary>
	public string? DeclaringType { get; init; }

	/// <summary>Its documentation's summary, where the caller asked for documentation and it has one.</summary>
	public string? Summary { get; init; }
}
