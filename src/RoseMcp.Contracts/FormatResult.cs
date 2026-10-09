namespace RoseMcp.Contracts;

/// <summary>What formatting did, and to which files.</summary>
public sealed record FormatResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	/// <summary>How many of the requested files were found and formatted.</summary>
	public required int FilesInspected { get; init; }
}
