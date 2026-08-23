namespace RoseMcp.Contracts;

/// <summary>Outcome of moving a type to its own file, including exactly what changed on disk.</summary>
public sealed record MoveTypeResult
{
	public required long Revision { get; init; }

	public required string TypeName { get; init; }

	public required string SourcePath { get; init; }

	public required string TargetPath { get; init; }

	/// <summary>False when this was a preview; nothing was written.</summary>
	public required bool Applied { get; init; }

	/// <summary>
	/// Using directives dropped because the split made them pointless -- from the file the type left
	/// as often as from the one it arrived in. Reported rather than left silent, since a using
	/// disappearing is the one part of this a caller would not have predicted.
	/// </summary>
	public required IReadOnlyList<string> RemovedUsings { get; init; }

	/// <summary>Unified diff of both files, so the caller can see the edit rather than trust it.</summary>
	public required string Diff { get; init; }

	public required IReadOnlyList<string> Notices { get; init; }
}
