namespace RoseMcp.Settings;

/// <summary>
/// What a person has accepted about the health of the solutions on this machine, and when each reason was
/// first seen, as <see cref="WorkspaceHealthFile"/> keeps it.
/// <para>
/// Plain strings and dates rather than the broker's types, because this project references nothing: a kind or
/// a choice this build does not know, written by a newer one, is carried through untouched rather than
/// failing to parse.
/// </para>
/// </summary>
public sealed record WorkspaceHealthRecord
{
	/// <summary>The shape this file is written in. A build finding a newer one reads it and refuses to write over it.</summary>
	public int Version { get; init; } = WorkspaceHealthFile.CurrentVersion;

	/// <summary>One entry per solution anything has been recorded for.</summary>
	public IReadOnlyList<SolutionHealthRecord> Solutions { get; init; } = [];
}
