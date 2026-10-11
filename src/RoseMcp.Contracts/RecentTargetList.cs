namespace RoseMcp.Contracts;

/// <summary>What a person debugged from a window lately, as <c>GET /operator/debug/recent</c> answers.</summary>
public sealed record RecentTargetList
{
	/// <summary>The most recent first.</summary>
	public IReadOnlyList<RecentTarget> Targets { get; init; } = [];
}
