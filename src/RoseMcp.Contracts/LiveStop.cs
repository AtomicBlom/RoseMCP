namespace RoseMcp.Contracts;

/// <summary>
/// A target held by the debugger: what stopped it, on which thread, and what will let it go.
/// <para>
/// <see cref="EventSequence"/> is the stop's identity, not decoration. It is the sequence of the
/// <see cref="LiveDebugEventKind.BreakpointHit"/> or <see cref="LiveDebugEventKind.StepComplete"/>
/// event that announced this stop, so a reader polling a session can tell a new stop from the same
/// one seen again -- which is what decides whether frames and locals have to be read afresh. Nothing
/// else distinguishes them: two hits of one breakpoint on one thread are identical in every other
/// field.
/// </para>
/// <para>
/// Everything read from a stop is valid only within it. A debugger's frames, threads and values are
/// live objects the runtime invalidates the moment the target moves, so a result carrying frames or
/// variables echoes the sequence it was read at rather than leaving a caller to assume.
/// </para>
/// </summary>
public sealed record LiveStop
{
	/// <summary>What stopped the target. Never <see cref="LiveExecutionState.Running"/>.</summary>
	public required LiveExecutionState State { get; init; }

	/// <summary>The thread the debugger is holding, when the runtime named one.</summary>
	public int? ThreadId { get; init; }

	/// <summary>
	/// The breakpoint that stopped it, or null for a step -- which is the same information
	/// <see cref="State"/> carries, said the other way round so a caller has the id to hand.
	/// </summary>
	public string? BreakpointId { get; init; }

	/// <summary>The sequence of the event that announced this stop, which identifies it.</summary>
	public required long EventSequence { get; init; }

	public required DateTime StoppedAtUtc { get; init; }

	/// <summary>Whether the safety timer or a person's hold decides when the target moves again.</summary>
	public required LiveStopResume Resume { get; init; }

	/// <summary>
	/// When the target resumes if nothing resumes it sooner: the safety timeout, or the hold's expiry.
	/// Always a moment rather than a duration, so a reader that polls does not have to guess how stale
	/// the number it is holding has become.
	/// </summary>
	public required DateTime ResumeDeadlineUtc { get; init; }
}
