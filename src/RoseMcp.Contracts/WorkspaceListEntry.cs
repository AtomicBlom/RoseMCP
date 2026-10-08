namespace RoseMcp.Contracts;

/// <summary>
/// One workspace as <c>rose_workspace_list</c> reports it: enough to choose it, name it and know
/// whether it is warm, and nothing that costs a call to the worker.
/// <para>
/// Leaner than <see cref="WorkspaceSummary"/> on purpose. That is the tray's row, carrying memory
/// figures and the last few operations, and a model asking what is open pays for every field of it
/// once per workspace. Every field here is the broker's own, so listing never waits on a load and
/// never counts as using a workspace.
/// </para>
/// <para>
/// No revision: a stopped worker has no snapshot to identify, and a loading one has none yet.
/// </para>
/// </summary>
public sealed record WorkspaceListEntry : WorkspaceScopedResult
{
	/// <summary>Where the workspace is in its life. <see cref="WorkspaceState.Unloaded"/> once its worker has stopped.</summary>
	public required WorkspaceState State { get; init; }

	/// <summary>
	/// Why the worker stopped -- Evicted, Crashed or StoppedByBroker -- and null while it runs. A
	/// stopped workspace is started again by the next call that needs it.
	/// </summary>
	public string? ExitReason { get; init; }

	/// <summary>Projects in the solution. Null until the first status report.</summary>
	public int? ProjectCount { get; init; }

	/// <summary>
	/// How long since a tool call last used it. Listing, status and opening an already-open
	/// workspace do not count, so watching a workspace does not keep it warm.
	/// </summary>
	public required TimeSpan IdleFor { get; init; }

	/// <summary>Operations running on it now. A busy workspace is never evicted.</summary>
	public required int Running { get; init; }
}
