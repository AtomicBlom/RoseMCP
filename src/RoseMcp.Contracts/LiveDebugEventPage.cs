namespace RoseMcp.Contracts;

/// <summary>
/// A window onto a session's buffered debug events. The agent is turn-based, so it does not listen
/// continuously; it reads a page, notes <see cref="NextCursor"/>, and passes that back as the
/// <c>after</c> argument next time to get only what has happened since.
/// <para>
/// The buffer is bounded, so a slow reader can fall behind the oldest kept event.
/// <see cref="OldestAvailable"/> says where the buffer now starts: if the reader's cursor is older
/// than that, events between the two were dropped, and <see cref="TotalObserved"/> versus the cursor
/// says how many.
/// </para>
/// </summary>
public sealed record LiveDebugEventPage
{
	/// <summary>The session's state now, so a reader learns of a fault or exit in the same call.</summary>
	public required LiveAppSessionState State { get; init; }

	/// <summary>The sequence of the newest event returned; pass it back as <c>after</c> next time.</summary>
	public required long NextCursor { get; init; }

	/// <summary>The sequence of the oldest event still buffered. A cursor below it missed events.</summary>
	public required long OldestAvailable { get; init; }

	/// <summary>How many events the session has observed in total, dropped or not.</summary>
	public required long TotalObserved { get; init; }

	/// <summary>The debuggee's process id, once attached.</summary>
	public int? TargetProcessId { get; init; }

	public IReadOnlyList<LiveDebugEvent> Events { get; init; } = [];

	/// <summary>
	/// How many events in this window were passed over by the kind filter. Zero when unfiltered. It
	/// is here so a small page is not mistaken for a quiet target -- the cursor has moved past these.
	/// </summary>
	public int Skipped { get; init; }
}
