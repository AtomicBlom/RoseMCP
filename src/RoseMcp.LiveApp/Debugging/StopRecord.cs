using ClrDebug;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// One stop of the debuggee: the thread it is held on, what put it there, and the two timers that
/// will end it if nobody does. Taken and given up as a unit, because the pieces are only meaningful
/// together -- a deadline belongs to a thread belongs to a reason, and a reader handed any one of
/// them alone cannot tell whether the other two still describe the same stop.
/// <para>
/// A stop is its own identity. Disposing a timer does not wait for a callback already running, so a
/// tick queued before a hold was taken would otherwise resume the target under the person reading
/// it; a timer carries the record it was armed for and does nothing once that is no longer the
/// record the session is holding. Reference equality is what decides that, so there is no counter
/// to keep in step.
/// </para>
/// <para>
/// Every member assumes the caller holds the session's gate. Nothing here locks: the session takes
/// a stop, hands it out and ends it under one lock, and a stop that could be mutated from outside
/// that lock is one whose deadline and timer could disagree.
/// </para>
/// </summary>
internal sealed class StopRecord(
	CorDebugThread thread,
	string? bindingId,
	LiveExecutionState state,
	int autoContinueSeconds) : IDisposable
{
	/// <summary>How long an unattended stop lasts before the safety timer resumes the target.</summary>
	internal const int DefaultAutoContinueSeconds = 30;

	private Timer? _autoContinueTimer;
	private Timer? _holdTimer;

	/// <summary>The thread the debugger is holding, which is the one a frame walk reads.</summary>
	internal CorDebugThread Thread { get; } = thread;

	/// <summary>The breakpoint that owns this stop, or null for a step or a manual pause.</summary>
	internal string? BindingId { get; } = bindingId;

	/// <summary>
	/// How the target came to be stopped, which is not inferable from what else is recorded: a manual
	/// pause and a completed step both arrive with no breakpoint owning them.
	/// </summary>
	internal LiveExecutionState State { get; } = state;

	/// <summary>
	/// What this stop's safety timeout was set to, kept so releasing a hold can re-arm the timer with
	/// the interval the breakpoint asked for rather than the default.
	/// </summary>
	internal int AutoContinueSeconds { get; } = autoContinueSeconds;

	/// <summary>When the stop happened, which with <see cref="EventSequence"/> is what tells a reader
	/// polling this session a new stop from the same one seen again.</summary>
	internal DateTime StoppedAtUtc { get; } = DateTime.UtcNow;

	/// <summary>
	/// The sequence of the event that announced this stop. Set once the event is buffered, which is
	/// after the stop exists, because the sentence it carries describes the stop.
	/// </summary>
	internal long EventSequence { get; set; }

	/// <summary>When the safety timer will resume the target, meaningful while nothing holds it.</summary>
	internal DateTime AutoContinueAtUtc { get; private set; }

	/// <summary>
	/// When an operator's hold expires, or null when nothing is holding this stop. A hold suspends the
	/// safety timer so a person can read a stack without it moving under them; it is bounded because a
	/// reader who walks away must not leave somebody's app frozen indefinitely.
	/// </summary>
	internal DateTime? HoldUntilUtc { get; private set; }

	/// <summary>Whether an operator is holding this stop rather than the safety timer owning it.</summary>
	internal bool IsHeld => HoldUntilUtc is not null;

	/// <summary>
	/// Arms the safety timer for this stop's own interval, measured from now, and stands down an
	/// operator's hold if there was one. Used both when the stop is taken and when a hold is released,
	/// because the interval means how long an unattended stop may last and a released stop has just
	/// become unattended.
	/// </summary>
	internal void ArmSafetyTimer(Action onDue)
	{
		StandDown();
		AutoContinueAtUtc = DateTime.UtcNow.AddSeconds(AutoContinueSeconds);
		_autoContinueTimer = Fire(onDue, TimeSpan.FromSeconds(AutoContinueSeconds));
	}

	/// <summary>
	/// Suspends the safety timer until <paramref name="duration"/> has passed, so somebody can read
	/// this stop without it moving. Asking again extends it.
	/// <para>
	/// The safety timer goes rather than being left to fire into a guard, so there is one timer per
	/// stop and the deadline the caller is told is the one that will actually arrive.
	/// </para>
	/// </summary>
	internal void HoldForOperator(TimeSpan duration, Action onDue)
	{
		StandDown();
		HoldUntilUtc = DateTime.UtcNow.Add(duration);
		_holdTimer = Fire(onDue, duration);
	}

	/// <summary>
	/// Stands down everything that would resume this stop: the safety timer, an operator's hold and
	/// its timer. Ending a stop disposes it, so a new stop never inherits the previous one's hold.
	/// </summary>
	public void Dispose() => StandDown();

	private void StandDown()
	{
		_autoContinueTimer?.Dispose();
		_autoContinueTimer = null;
		_holdTimer?.Dispose();
		_holdTimer = null;
		HoldUntilUtc = null;
	}

	private static Timer Fire(Action onDue, TimeSpan due) =>
		new(_ => onDue(), null, due, Timeout.InfiniteTimeSpan);

	/// <summary>
	/// This stop as a caller sees it. Built rather than stored, because the deadline it reports
	/// depends on which timer currently owns the stop.
	/// </summary>
	internal LiveStop Describe() => new()
	{
		State = State,
		ThreadId = CorDebugInspector.TryThreadId(Thread),
		BreakpointId = BindingId,
		EventSequence = EventSequence,
		StoppedAtUtc = StoppedAtUtc,
		Resume = IsHeld ? LiveStopResume.HeldByOperator : LiveStopResume.AutoContinue,
		ResumeDeadlineUtc = HoldUntilUtc ?? AutoContinueAtUtc,
	};
}
