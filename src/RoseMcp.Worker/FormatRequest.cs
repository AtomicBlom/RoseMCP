namespace RoseMcp.Worker;

/// <summary>Which files to format, and whether to write the result.</summary>
public sealed record FormatRequest
{
	/// <summary>
	/// The files to format. Named explicitly rather than inferred, because the caller knows which
	/// files it just wrote and formatting anything else would put changes in a diff nobody asked for.
	/// </summary>
	public required IReadOnlyList<string> FilePaths { get; init; }

	/// <summary>False returns the diff without touching disk, which doubles as a formatting check.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Also drop using directives the file does not need. Off by default: a formatter that quietly
	/// deletes code is a surprise, even when the code is dead.
	/// </summary>
	public bool RemoveUnusedUsings { get; init; }

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
