namespace RoseMcp.Contracts;

/// <summary>What formatting did, and to which files.</summary>
public sealed record FormatResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	/// <summary>How many of the requested files were found and formatted.</summary>
	public required int FilesInspected { get; init; }


	/// <summary>False when this was a preview; nothing was written.</summary>
	public required bool Applied { get; init; }

	/// <summary>Unified diff of every file that changed, so the caller can see the edit.</summary>
	public required string Diff { get; init; }
}
