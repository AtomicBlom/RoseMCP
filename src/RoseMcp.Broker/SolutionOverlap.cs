namespace RoseMcp.Broker;

/// <summary>
/// Another solution beside this one that compiles some of the same files.
/// </summary>
public sealed record SolutionOverlap
{
	public required string SolutionPath { get; init; }

	/// <summary>How many of the files a change touched this solution also builds.</summary>
	public required int SharedFileCount { get; init; }
}
