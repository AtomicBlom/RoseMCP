namespace RoseMcp.Contracts;

/// <summary>
/// One file a recorded write changed, or would have changed where it was a preview, and its diff.
/// <para>
/// The record keeps the diff a caller is usually not shown: what an agent wrote is the thing a person
/// watching it wants to read, even though the agent itself has no use for its own code read back.
/// </para>
/// </summary>
public sealed record ActivityFileChange
{
	/// <summary>The file, absolute, as the worker that wrote it named it.</summary>
	public required string FilePath { get; init; }

	/// <summary>See <see cref="ChangedFile.Lines"/>.</summary>
	public string? Lines { get; init; }

	/// <summary>See <see cref="ChangedFile.Created"/>.</summary>
	public bool? Created { get; init; }

	/// <summary>See <see cref="ChangedFile.Normalised"/>.</summary>
	public string? Normalised { get; init; }

	/// <summary>
	/// This file's part of the write's unified diff, taken from the document before and after. Cut where it is
	/// longer than the record keeps, and absent where the write's diff had nothing for it, a change of line
	/// endings alone, or where the call's diffs together had already reached what the record keeps of them; a
	/// listing leaves it out, and the entry read by its id has it.
	/// </summary>
	public string? Diff { get; init; }

	/// <summary>How long the diff really is, where <see cref="Diff"/> was cut or left out for its length; absent otherwise.</summary>
	public int? DiffFullLength { get; init; }
}
