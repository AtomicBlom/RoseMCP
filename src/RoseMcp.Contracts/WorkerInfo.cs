namespace RoseMcp.Contracts;

/// <summary>What a worker can say about itself, cheaply and without loading anything.</summary>
public sealed record WorkerInfo
{
	public required int ProcessId { get; init; }

	public required string SolutionPath { get; init; }

	/// <summary>
	/// Managed heap size. Worth reporting alongside the working set the broker samples externally,
	/// because for a Roslyn host the gap between the two is mostly compilation caches.
	/// </summary>
	public required long ManagedHeapBytes { get; init; }
}
