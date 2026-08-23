namespace RoseMcp.Worker;

/// <summary>What a set of solution changes did, or would do.</summary>
public sealed record WriteOutcome
{
	public required IReadOnlyList<string> ChangedFiles { get; init; }

	public required string Diff { get; init; }
}
