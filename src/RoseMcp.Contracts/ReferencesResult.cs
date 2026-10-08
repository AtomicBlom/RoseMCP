using System.Text.Json.Serialization;

namespace RoseMcp.Contracts;

/// <summary>
/// Every reference to a symbol across the solution: listed by file where there are few enough to
/// read, and described by their shape where there are not.
/// </summary>
public sealed record ReferencesResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>
	/// The symbol these are references to, as an address: pass it back as <c>symbol</c> to any rose_*
	/// tool. Not repeated per reference, because a location already names the member it sits inside and
	/// an address on each would grow the very answer the narrowing arguments exist to shrink.
	/// </summary>
	public string? Address { get; init; }

	public required string Symbol { get; init; }

	public required IReadOnlyList<SourceLocation> Definitions { get; init; }

	/// <summary>
	/// The references the filters kept, by the file they are in, so what every reference in a file
	/// shares -- its path, its project, whether that is a test project, the generator that wrote it --
	/// is said once. Empty where they are described by <see cref="Shape"/> instead.
	/// </summary>
	public required IReadOnlyList<ReferenceFile> Files { get; init; }

	/// <summary>How many references the filters kept, listed or not.</summary>
	public required int TotalCount { get; init; }

	/// <summary>
	/// True where there were more references than the caller's <c>maxResults</c>, so they are described
	/// by <see cref="Shape"/> rather than listed. Raising <c>maxResults</c> to <see cref="TotalCount"/>
	/// lists them; narrowing lists fewer. Never set for <c>definitionsOnly</c>, which asked for no list.
	/// </summary>
	public required bool Truncated { get; init; }

	/// <summary>
	/// How the references divide, given wherever the list is not the whole answer: past
	/// <c>maxResults</c>, for <c>definitionsOnly</c>, and where the filters kept nothing -- in which case
	/// it describes every reference the symbol has, and <see cref="Notices"/> says so.
	/// </summary>
	public ReferenceShape? Shape { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}

/// <summary>One file's references.</summary>
public sealed record ReferenceFile
{
	public required string FilePath { get; init; }

	/// <summary>The project compiling the file.</summary>
	public string? Project { get; init; }

	/// <summary>
	/// True where that project references a test framework: a use from a test usually means the symbol
	/// can change and the test follows. Absent when false.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsTestProject { get; init; }

	/// <summary>
	/// The hint name of the generated document, where the file is a generator's output rather than a
	/// file on disk; it reads back through rose_read_generated_document.
	/// </summary>
	public string? GeneratedHintName { get; init; }

	/// <summary>Its references, in the order they appear.</summary>
	public required IReadOnlyList<ReferenceSite> References { get; init; }
}

/// <summary>One reference, inside the file that <see cref="ReferenceFile"/> names.</summary>
public sealed record ReferenceSite
{
	public required int Line { get; init; }

	public required int Column { get; init; }

	/// <summary>The member it sits inside, as <c>Type.Member</c>: the value <c>containingMember</c> takes.</summary>
	public string? ContainingMember { get; init; }

	/// <summary>The source line itself, where the caller asked for previews.</summary>
	public string? Preview { get; init; }
}

/// <summary>
/// How a set of references divides along each facet a reference carries. Every group is keyed by the
/// value the narrowing argument of the same name takes, so a group read here is a question the next
/// call can ask.
/// </summary>
public sealed record ReferenceShape
{
	/// <summary>How many references this describes.</summary>
	public required int Total { get; init; }

	/// <summary>How many of them are in test projects.</summary>
	public required int InTestProjects { get; init; }

	/// <summary>How many of them are in source-generated code.</summary>
	public required int InGeneratedCode { get; init; }

	/// <summary>Every project with a reference, most references first.</summary>
	public required IReadOnlyList<ProjectReferenceCount> Projects { get; init; }

	/// <summary>
	/// The members holding the most references, most first, up to a cap: <see cref="MemberCount"/> says
	/// how many there are in all.
	/// </summary>
	public required IReadOnlyList<MemberReferenceCount> Members { get; init; }

	/// <summary>How many members hold a reference.</summary>
	public required int MemberCount { get; init; }
}

/// <summary>How many references one project compiles.</summary>
public sealed record ProjectReferenceCount
{
	public required string Project { get; init; }

	public required int Count { get; init; }

	/// <summary>Absent when false.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsTestProject { get; init; }
}

/// <summary>How many references sit inside one member.</summary>
public sealed record MemberReferenceCount
{
	public required string ContainingMember { get; init; }

	public required int Count { get; init; }
}
