namespace RoseMcp.Contracts;

/// <summary>
/// What became of each stopping breakpoint one call asked for: the breakpoint twin of
/// <see cref="LiveTracepointBatch"/>, and refused one entry at a time for the same reason.
/// <see cref="Set"/> counts the ones that were set, bound or not.
/// </summary>
public sealed record LiveBreakpointBatch : LiveResult
{
	/// <summary>How many were set, including those still waiting for their module to load.</summary>
	public int Set { get; init; }

	/// <summary>How many were asked for.</summary>
	public int Total => Results.Count;

	public IReadOnlyList<LiveBreakpointOutcome> Results { get; init; } = [];

	/// <summary>Remarks about the batch as a whole, such as one location asked for twice.</summary>
	public IReadOnlyList<string> Notes { get; init; } = [];
}
