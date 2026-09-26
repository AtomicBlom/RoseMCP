using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>A structural rewrite: the rules, where to apply them, and whether to write the result.</summary>
public sealed record ReplacePatternRequest
{
	/// <summary>The rules, in the order they are tried at each site.</summary>
	public required IReadOnlyList<PatternRule> Rules { get; init; }

	/// <summary>Namespaces the patterns are written against, beyond what each project already imports.</summary>
	public IReadOnlyList<string> Usings { get; init; } = [];

	/// <summary>
	/// Files or directories to rewrite in, as absolute paths. Empty means the whole solution, which is
	/// the scope a mass rewrite usually wants and the one a caller would otherwise have to list.
	/// </summary>
	public IReadOnlyList<string> FilePaths { get; init; } = [];

	/// <summary>False returns the same summary without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>Compile the changed projects afterwards and report what the change broke.</summary>
	public bool Verify { get; init; } = true;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
