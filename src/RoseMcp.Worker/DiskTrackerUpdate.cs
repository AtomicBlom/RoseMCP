using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// What a sweep worked out about the tracking table and has not applied yet.
/// <para>
/// A stamp equal to disk is a claim that the snapshot in hand holds that file's current text, and
/// a document id is a claim that the snapshot contains that document. Reconciliation can still end
/// in a reload, and a reload can throw, so recording either as the sweep goes claims it of a
/// snapshot nobody kept. The next sweep then finds every stamp current and every structural file
/// unchanged, reports nothing to do, and serves the old snapshot with nothing to say it is stale --
/// the silent staleness the barrier exists to rule out. A phantom id is worse again: Roslyn rejects
/// a document id the solution does not contain, so the first edit to that file fails and so does
/// every read after it.
/// </para>
/// <para>
/// Opaque on purpose. The session's part is to hand it to <see cref="DiskSynchronizer.Commit"/> in
/// the same step that takes the snapshot it describes, and to drop it on every other path.
/// </para>
/// </summary>
public sealed class DiskTrackerUpdate
{
	/// <summary>Documents to start tracking, or to restamp at what this sweep saw.</summary>
	internal List<TrackedDocument> Tracked { get; } = [];

	/// <summary>Documents whose files have gone, to stop tracking.</summary>
	internal List<DocumentId> Untracked { get; } = [];

	/// <summary>Build-influencing files and the stamps this sweep read for them.</summary>
	internal List<KeyValuePair<string, FileStamp?>> Structural { get; } = [];

	/// <summary>Files reported as being outside the build, so the notice is not repeated.</summary>
	internal List<string> Declined { get; } = [];
}
