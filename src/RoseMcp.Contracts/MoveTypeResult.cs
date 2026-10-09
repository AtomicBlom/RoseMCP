namespace RoseMcp.Contracts;

/// <summary>
/// Outcome of moving a type to its own file. The new file is the entry in changedFiles marked created.
/// </summary>
public sealed record MoveTypeResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	public required string TypeName { get; init; }

	/// <summary>
	/// Using directives dropped because the split made them pointless -- from the file the type left
	/// as often as from the one it arrived in. Reported rather than left silent, since a using
	/// disappearing is the one part of this a caller would not have predicted.
	/// </summary>
	public required IReadOnlyList<string> RemovedUsings { get; init; }
}
