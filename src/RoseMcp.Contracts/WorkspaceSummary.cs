namespace RoseMcp.Contracts;

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

	/// <summary>What this worker is doing right now, oldest first.</summary>
	public IReadOnlyList<WorkerActivity> Running { get; init; } = [];

	/// <summary>
	/// The last few operations to finish, newest first. Kept because the interesting question is
	/// usually "what did the agent just ask for", and by the time anyone looks the call is over.
	/// </summary>
	public IReadOnlyList<WorkerActivity> Recent { get; init; } = [];
}
