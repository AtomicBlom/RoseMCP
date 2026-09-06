using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>Outcome of one reconciliation sweep.</summary>
public sealed record DiskSyncResult
{
	public required Solution Solution { get; init; }

	public required int ChangedCount { get; init; }

	public required int RemovedCount { get; init; }

	/// <summary>
	/// A project file, props file, or the solution itself changed. Text patching cannot represent
	/// that, so the caller has to reload rather than carry on with a patched snapshot.
	/// </summary>
	public required bool StructuralChange { get; init; }

	/// <summary>Files that could not be read this sweep, usually because a write was in progress.</summary>
	public required IReadOnlyList<string> Deferred { get; init; }

	/// <summary>
	/// The tracking changes this sweep worked out. Applied by the caller in the same step that takes
	/// the snapshot above, never as the sweep goes.
	/// </summary>
	public required DiskTrackerUpdate Tracker { get; init; }

	public bool AnythingChanged => ChangedCount > 0 || RemovedCount > 0 || StructuralChange;
}
