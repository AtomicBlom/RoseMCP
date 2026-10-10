namespace RoseMcp.Settings;

/// <summary>What is recorded for one solution.</summary>
public sealed record SolutionHealthRecord
{
	/// <summary>The solution's absolute path, which is what names it: see the decision on where accepted reasons live.</summary>
	public required string Solution { get; init; }

	/// <summary>Each kind of reason the person accepted, per project.</summary>
	public IReadOnlyList<AcceptedReasonRecord> Accepted { get; init; } = [];

	/// <summary>When each kind of reason was first and last seen, per project.</summary>
	public IReadOnlyList<SeenReasonRecord> Seen { get; init; } = [];
}
