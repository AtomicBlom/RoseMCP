namespace RoseMcp.Settings;

/// <summary>One kind of reason accepted for one project, or for the whole solution where the project is empty.</summary>
public sealed record AcceptedReasonRecord
{
	/// <summary>The kind, as the broker names it.</summary>
	public required string Kind { get; init; }

	/// <summary>The project, by name; empty for the whole solution.</summary>
	public string Project { get; init; } = string.Empty;

	/// <summary>When it was accepted.</summary>
	public required DateTime AcceptedUtc { get; init; }

	/// <summary>What agents read about it, by name: RoseLine, Note or Nothing.</summary>
	public string AgentsRead { get; init; } = "RoseLine";

	/// <summary>The person's note for agents.</summary>
	public string? Note { get; init; }
}
