namespace RoseMcp.Contracts;

/// <summary>
/// How the solution's projects depend on each other, which decides where new code can go and what
/// an edit can reach.
/// </summary>
public sealed record ProjectGraphResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required IReadOnlyList<ProjectNode> Projects { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}

/// <summary>One project and what it can see.</summary>
public sealed record ProjectNode
{
	public required string Name { get; init; }

	public string? FilePath { get; init; }

	/// <summary>The target framework, where the project is loaded under exactly one.</summary>
	public string? TargetFramework { get; init; }

	/// <summary>The assembly it produces, so a caller can tell what a stale bin folder holds.</summary>
	public string? OutputPath { get; init; }

	/// <summary>
	/// Whether it references a test framework. A project that does is one whose code follows a
	/// change rather than constrains it.
	/// </summary>
	public bool IsTestProject { get; init; }

	/// <summary>Projects it references directly, which is what its code can name.</summary>
	public required IReadOnlyList<string> References { get; init; }

	/// <summary>
	/// Projects that reference it, directly or through another. This is the set a change to a public
	/// member reaches, and the reason it is worth asking before making one.
	/// </summary>
	public required IReadOnlyList<string> ReferencedBy { get; init; }

	public required int DocumentCount { get; init; }
}
