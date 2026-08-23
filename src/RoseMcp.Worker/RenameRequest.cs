namespace RoseMcp.Worker;

/// <summary>Options a caller can vary on a rename.</summary>
public sealed record RenameRequest
{
	public required string FilePath { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	public required string NewName { get; init; }

	public bool RenameOverloads { get; init; }

	public bool RenameInComments { get; init; }

	public bool RenameInStrings { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Fail rather than apply if the workspace has moved past this revision. Matters when more than
	/// one client shares a broker in http mode.
	/// </summary>
	public long? ExpectedRevision { get; init; }
}
