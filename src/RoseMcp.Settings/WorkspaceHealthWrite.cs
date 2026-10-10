namespace RoseMcp.Settings;

/// <summary>
/// What <see cref="WorkspaceHealthFile.Update"/> did: the record it wrote, or what stopped it, in words a person
/// can act on. An answer rather than an exception, for the reason the file never throws: nothing in it is
/// load-bearing, and the caller decides whether a failure is worth saying.
/// </summary>
public sealed record WorkspaceHealthWrite
{
	/// <summary>The record as it now stands on disk, or null where it could not be written.</summary>
	public WorkspaceHealthRecord? Record { get; private init; }

	/// <summary>What stopped the write, or null where it landed.</summary>
	public string? Failure { get; private init; }

	/// <summary>Whether the change is on disk.</summary>
	public bool Succeeded => Record is not null;

	/// <summary>A write that landed, or a change that needed none.</summary>
	public static WorkspaceHealthWrite Wrote(WorkspaceHealthRecord record) => new() { Record = record };

	/// <summary>A write that did not land, and why.</summary>
	public static WorkspaceHealthWrite Failed(string failure) => new() { Failure = failure };
}
