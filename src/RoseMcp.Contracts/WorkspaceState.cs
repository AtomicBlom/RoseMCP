namespace RoseMcp.Contracts;

/// <summary>Lifecycle of a single loaded solution, as reported to callers and to the tray UI.</summary>
public enum WorkspaceState
{
	/// <summary>
	/// The solution is being opened. Reads block until this completes -- with one exception, and it is
	/// the reason this value is worth reporting rather than merely passing through:
	/// <c>rose_workspace_open</c> answers while it is true, so a caller can start a load and get on
	/// with something else. That makes this state the thing that says a result is provisional. A
	/// summary carrying it has no project list or load diagnostics yet, and its empty lists mean "not
	/// established", not "none" -- which is exactly the distinction a null field could not draw,
	/// because an absent value says only that something is unknown while this says what is happening
	/// and that asking again will answer.
	/// </summary>
	Loading,

	/// <summary>Loaded, with every project reporting a successful design-time build.</summary>
	Loaded,

	/// <summary>
	/// Loaded, but something will make answers wrong or incomplete -- most often an in-solution
	/// source generator whose output assembly is missing, which silently yields zero generated
	/// documents. Details are in the status report.
	/// </summary>
	Degraded,

	/// <summary>
	/// The solution file has gone missing and the unload grace timer is running. Reads are still
	/// served from the last good snapshot, flagged stale, in case this is an atomic save or a
	/// branch switch that puts the file straight back.
	/// </summary>
	PendingUnload,

	/// <summary>The worker crashed, hung, or failed to load. Recoverable only by a hard reload.</summary>
	Faulted,

	/// <summary>The solution is gone for good and the worker has exited.</summary>
	Unloaded,
}
