namespace RoseMcp.Settings;

/// <summary>One kind of reason accepted for one project, or for one subject of a reason naming no project.</summary>
public sealed record AcceptedReasonRecord
{
	/// <summary>The kind, as the broker names it.</summary>
	public required string Kind { get; init; }

	/// <summary>What it covers: a project by name, or, for a reason that names no project, its subject -- the assembly that would not load, the platform chosen.</summary>
	public string Covering { get; init; } = string.Empty;

	/// <summary>
	/// What a version 1 record called <see cref="Covering"/>, read so its acceptances survive and moved into
	/// <see cref="Covering"/> as the file is read; never written.
	/// </summary>
	public string? Project { get; init; }

	/// <summary>When it was accepted.</summary>
	public required DateTime AcceptedUtc { get; init; }

	/// <summary>What agents read about it, by name: RoseLine, Note or Nothing.</summary>
	public string AgentsRead { get; init; } = "RoseLine";

	/// <summary>The person's note for agents.</summary>
	public string? Note { get; init; }
}
