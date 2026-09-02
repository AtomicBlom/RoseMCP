namespace RoseMcp.Contracts;

/// <summary>One row of the tray window, and of GET /admin/workspaces.</summary>
public sealed record WorkspaceSummary
{
	public required string SolutionPath { get; init; }

	public required string DisplayName { get; init; }

	public required bool Alive { get; init; }

	public required string ExitReason { get; init; }

	/// <summary>
	/// Where the workspace is in its life, as far as the broker can tell. The process answers for a
	/// dead worker; for a live one it is the last status report to pass through -- the one the
	/// broker asks for on connect, or any a client has asked for since -- and
	/// <see cref="WorkspaceState.Loading"/> until the first arrives.
	/// </summary>
	public required WorkspaceState State { get; init; }

	public required DateTime StartedUtc { get; init; }

	public required TimeSpan Uptime { get; init; }

	public int? ProcessId { get; init; }

	/// <summary>Sampled from the process rather than self-reported, so a hung worker still reports.</summary>
	public long? WorkingSetBytes { get; init; }

	public long? PrivateMemoryBytes { get; init; }

	/// <summary>Last value the worker reported for its managed heap, when it was still answering.</summary>
	public long? ManagedHeapBytes { get; init; }

	/// <summary>
	/// The MSBuild configuration and platform the solution was loaded under, as
	/// <c>Configuration|Platform</c>. Worth a place in a summary because the wrong one is the usual
	/// reason a whole solution looks broken, and it is the one fact about a load that cannot be
	/// seen from outside the process.
	/// </summary>
	public string? BuildConfiguration { get; init; }

	/// <summary>Projects in the solution. Null until the first status report.</summary>
	public int? ProjectCount { get; init; }

	/// <summary>Projects whose design-time build failed, by name. Answers about them are unreliable.</summary>
	public IReadOnlyList<string> FailedProjects { get; init; } = [];

	/// <summary>Why the answers cannot be fully trusted, each with its fix. Empty when they can.</summary>
	public IReadOnlyList<string> DegradedReasons { get; init; } = [];

	/// <summary>Decisions the load made unasked that are worth knowing about, chiefly a configuration it chose.</summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

	/// <summary>
	/// How long the initial load took, from the worker connecting to its first status answer. Null
	/// while it is still going, and if it never finished.
	/// </summary>
	public double? LoadSeconds { get; init; }

	/// <summary>What this worker is doing right now, oldest first.</summary>
	public IReadOnlyList<WorkerActivity> Running { get; init; } = [];

	/// <summary>
	/// The last few operations to finish, newest first. Kept because the interesting question is
	/// usually "what did the agent just ask for", and by the time anyone looks the call is over.
	/// </summary>
	public IReadOnlyList<WorkerActivity> Recent { get; init; } = [];
}
