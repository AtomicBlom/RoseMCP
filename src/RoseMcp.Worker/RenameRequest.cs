namespace RoseMcp.Worker;

/// <summary>Options a caller can vary on a rename.</summary>
public sealed record RenameRequest
{
	/// <summary>
	/// Which symbol to rename, by name or by position. Renames arrive in batches more than any other
	/// edit, and a position found by reading the file is wrong the moment an earlier one lands -- so
	/// the name is the spelling that survives a batch.
	/// </summary>
	public required SymbolTarget Target { get; init; }

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
