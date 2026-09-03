namespace RoseMcp.Contracts;

/// <summary>Which projects would be executed as last built rather than as written.</summary>
public sealed record BuildFreshnessReport : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required IReadOnlyList<ProjectFreshness> Projects { get; init; }

	/// <summary>How many of them are stale, so the answer is readable without reading the list.</summary>
	public required int StaleCount { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}
