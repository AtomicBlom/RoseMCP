namespace RoseMcp.Worker;

/// <summary>Which type to move, and where to put it.</summary>
public sealed record MoveTypeRequest
{
	public required string FilePath { get; init; }

	/// <summary>
	/// The type's name as written, without type parameters. Names rather than a file position
	/// because splitting a file is something a caller decides after reading it, when the names are
	/// what it has to hand.
	/// </summary>
	public required string TypeName { get; init; }

	/// <summary>Where to put it. Defaults to a file named after the type, beside the source.</summary>
	public string? TargetPath { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Fail rather than apply if the workspace has moved past this revision. Matters when more than
	/// one client shares a broker in http mode.
	/// </summary>
	public long? ExpectedRevision { get; init; }
}
