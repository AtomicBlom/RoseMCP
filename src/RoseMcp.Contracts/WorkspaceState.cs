namespace RoseMcp.Contracts;

/// <summary>Lifecycle of a single loaded solution, as reported to callers and to the tray UI.</summary>
public enum WorkspaceState
{
	/// <summary>The solution is being opened. Reads block until this completes.</summary>
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
