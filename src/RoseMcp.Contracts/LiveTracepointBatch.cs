namespace RoseMcp.Contracts;

/// <summary>
/// What became of each tracepoint one call asked for. <see cref="Results"/> is one entry per
/// request, in the order they were given; <see cref="Added"/> counts the ones that were added, bound
/// or not. A request refused for its own reason -- a location, message or condition that does not
/// parse -- is that entry's status and never stops the others, because the requests are independent
/// and a partial result the caller can read beats an all-or-nothing refusal it has to retry piece by
/// piece. <see cref="Notes"/> carries what is true of the batch rather than of one entry.
/// </summary>
public sealed record LiveTracepointBatch : LiveResult
{
	/// <summary>How many were added, including those still waiting for their module to load.</summary>
	public int Added { get; init; }

	/// <summary>How many were asked for.</summary>
	public int Total => Results.Count;

	public IReadOnlyList<LiveTracepointOutcome> Results { get; init; } = [];

	/// <summary>Remarks about the batch as a whole, such as one location asked for twice.</summary>
	public IReadOnlyList<string> Notes { get; init; } = [];
}
