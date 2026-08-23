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

/// <summary>One row of the tray window, and of GET /admin/workspaces.</summary>
public sealed record WorkspaceSummary
{
	public required string SolutionPath { get; init; }

	public required string DisplayName { get; init; }

	public required bool Alive { get; init; }

	public required string ExitReason { get; init; }

	public required DateTime StartedUtc { get; init; }

	public required TimeSpan Uptime { get; init; }

	public int? ProcessId { get; init; }

	/// <summary>Sampled from the process rather than self-reported, so a hung worker still reports.</summary>
	public long? WorkingSetBytes { get; init; }

	public long? PrivateMemoryBytes { get; init; }

	/// <summary>Last value the worker reported for its managed heap, when it was still answering.</summary>
	public long? ManagedHeapBytes { get; init; }
}
