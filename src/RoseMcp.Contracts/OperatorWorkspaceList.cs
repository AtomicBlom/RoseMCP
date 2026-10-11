namespace RoseMcp.Contracts;

/// <summary>
/// Every workspace the broker holds a worker for, running or stopped, as <c>GET /operator/workspaces</c>
/// answers, with the rule that closes an idle one and the version of the view that a long poll waits past.
/// </summary>
public sealed record OperatorWorkspaceList
{
	/// <summary>One row per worker, ordered by solution path.</summary>
	public IReadOnlyList<OperatorWorkspace> Workspaces { get; init; } = [];

	/// <summary>
	/// How long a worker may sit unused before it is closed to free its memory. Null where nothing is closed for
	/// being idle, which is a broker serving one client over stdio.
	/// </summary>
	public TimeSpan? IdleEvictionAfter { get; init; }

	/// <summary>
	/// What the view held when it was read, as an opaque string: passed back as <c>after</c> with a
	/// <c>waitSeconds</c>, the next read waits until the view holds something else. Memory, uptime, idle time and
	/// how long a running operation has taken are left out of it, since they change on every read; a window
	/// counts idle time on from <see cref="OperatorWorkspace.LastUsedUtc"/> and reads memory at
	/// <c>GET /operator/memory</c>.
	/// </summary>
	public required string Version { get; init; }
}
