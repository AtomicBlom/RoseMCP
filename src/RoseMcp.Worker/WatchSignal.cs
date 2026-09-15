namespace RoseMcp.Worker;

/// <summary>What the watcher noticed since the last read barrier looked.</summary>
[Flags]
public enum WatchSignal
{
	None = 0,

	/// <summary>Ordinary edits. The stat sweep will pick these up.</summary>
	FileChanges = 1,

	/// <summary>
	/// The event stream cannot be trusted -- the watcher failed or could not start -- or HEAD was rewritten,
	/// so the next barrier must do a full reconcile. How many files changed is never a reason on its own.
	/// </summary>
	FullResyncRequired = 2,

	/// <summary>A git operation is in flight. Reconciling now would read a half-written tree.</summary>
	GitOperationInFlight = 4,

	/// <summary>The solution file is no longer where it should be.</summary>
	SolutionMissing = 8,
}
