using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// An immutable view of the solution, already reconciled with disk, plus the revision it belongs
/// to. Expensive analysis runs against this off the session's writer, which is safe precisely
/// because Roslyn solutions are immutable.
/// </summary>
public sealed record WorkspaceSnapshot
{
	public required Solution Solution { get; init; }

	/// <summary>The revision this snapshot represents. Two results sharing it describe one world.</summary>
	public required long Revision { get; init; }

	/// <summary>
	/// True when the snapshot could not be fully reconciled -- the solution file has gone missing
	/// and the unload grace period is running, so this is the last known good state rather than
	/// current truth.
	/// </summary>
	public bool Stale { get; init; }

	/// <summary>
	/// Things that happened during reconciliation and that a caller should know about: projects
	/// reloaded, documents dropped, files that could not be read this time.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

	/// <summary>
	/// Refuses a write whose caller was looking at an older world than this one.
	/// <para>
	/// One place rather than the same three lines in ten services, and the sentence now says what to
	/// pass next. "Re-read and try again" left the caller to work out that the revision they need is
	/// the one their next read returns -- or that they may simply leave the argument off, which is
	/// what a caller who did not mean to guard the write wanted in the first place.
	/// </para>
	/// </summary>
	/// <param name="expected">The revision the caller last saw, or null where they did not say.</param>
	/// <exception cref="InvalidOperationException">The workspace has moved past it.</exception>
	public void RefuseIfMoved(long? expected)
	{
		if (expected is not { } wanted || wanted == Revision) return;

		throw new InvalidOperationException(
			$"The workspace is at revision {Revision}, not the expected {wanted}. Something changed "
				+ "underneath this request -- another tool's edit, or a change on disk. Read again and "
				+ "pass the revision that read returns, or leave expectedRevision off to apply regardless.");
	}
}
