namespace RoseMcp.Worker;

/// <summary>What the watcher noticed since the last read barrier looked.</summary>
[Flags]
public enum WatchSignal
{
	None = 0,

	/// <summary>Ordinary edits. The stat sweep will pick these up.</summary>
	FileChanges = 1,

	/// <summary>
	/// The barrier must reload whatever the sweep finds. Set by the barrier itself when the solution file
	/// comes back after going missing, since the snapshot served stale meanwhile describes nothing about the
	/// file that returned. The watcher never sets it: how many files changed, and anything git did, are never
	/// reasons on their own.
	/// </summary>
	FullResyncRequired = 2,

	/// <summary>A git operation is in flight. Reconciling now would read a half-written tree.</summary>
	GitOperationInFlight = 4,

	/// <summary>The solution file is no longer where it should be.</summary>
	SolutionMissing = 8,

	/// <summary>
	/// The watcher lost events: its buffer overflowed, its directory went, or it could not start. Every read
	/// stats, walks and probes regardless, so this matters only where the watcher's own list is the answer --
	/// a build file changing that a project which could not be evaluated might import.
	/// </summary>
	EventsLost = 16,
}
