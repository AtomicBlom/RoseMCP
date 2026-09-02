namespace RoseMcp.Contracts;

/// <summary>Outcome of a rename, including exactly what changed on disk.</summary>
public sealed record RenameResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required string OldName { get; init; }

	public required string NewName { get; init; }

	/// <summary>False when this was a preview; nothing was written.</summary>
	public required bool Applied { get; init; }

	public required int FilesChanged { get; init; }

	/// <summary>Unified diff of every changed file, so the caller can see the edit rather than trust it.</summary>
	public required string Diff { get; init; }

	/// <summary>
	/// Places Roslyn flagged as conflicting -- the new name would bind to something else, or shadow
	/// an existing member. Surfaced rather than silently applied.
	/// </summary>
	public required IReadOnlyList<string> Conflicts { get; init; }

	/// <summary>
	/// Places in markup that name the old identifier. Not changed -- markup is text to the compiler,
	/// so a binding that no longer resolves builds and runs and shows nothing. Reported so the one
	/// breakage no C# tool can see is at least visible.
	/// </summary>
	public IReadOnlyList<XamlMention> XamlMentions { get; init; } = [];

	public required IReadOnlyList<string> Notices { get; init; }
}
