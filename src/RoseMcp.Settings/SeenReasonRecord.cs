namespace RoseMcp.Settings;

/// <summary>When one kind of reason was first seen for one project, and when it was last seen to the day.</summary>
public sealed record SeenReasonRecord
{
	/// <summary>The kind, as the broker names it.</summary>
	public required string Kind { get; init; }

	/// <summary>The project, by name; empty for the whole solution.</summary>
	public string Project { get; init; } = string.Empty;

	/// <summary>When it was first seen.</summary>
	public required DateTime FirstSeenUtc { get; init; }

	/// <summary>When it was last seen, kept to the day so that seeing it again is not a write.</summary>
	public required DateTime LastSeenUtc { get; init; }
}
