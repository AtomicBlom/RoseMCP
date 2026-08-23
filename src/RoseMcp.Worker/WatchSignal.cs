namespace RoseMcp.Worker;

/// <summary>What the watcher noticed since the last read barrier looked.</summary>
[Flags]
public enum WatchSignal
{
	None = 0,

	/// <summary>Ordinary edits. The stat sweep will pick these up.</summary>
	FileChanges = 1,

	/// <summary>
	/// Too much moved at once, or the watcher itself fell over. Incremental absorption is no longer
	/// trustworthy and the next barrier must do a full reconcile.
	/// </summary>
	FullResyncRequired = 2,

	/// <summary>A git operation is in flight. Reconciling now would read a half-written tree.</summary>
	GitOperationInFlight = 4,

	/// <summary>The solution file is no longer where it should be.</summary>
	SolutionMissing = 8,
}
