using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// What it takes to get a debugger off a process without killing it: how many times to try, how to
/// bring the target to rest first, which failures are worth retrying, and what to say when it did
/// not work.
/// <para>
/// This is policy rather than state. The transitions a detach makes -- ending the stop, entering
/// the window, coming back out of it -- stay with the session, because they are writes to the one
/// value that says what the target is doing and a second writer is how that value starts
/// disagreeing with itself. What lives here is the counting and the deciding.
/// </para>
/// <para>
/// The stop counter is the exception to "no state": it is written from mscordbi's thread and read
/// from the detaching one, so it moves through <see cref="Interlocked"/> rather than a lock, which
/// the detaching thread is holding. It sits here because the loop that interprets it is here, and a
/// counter kept away from the only code that reads it is one nobody can check.
/// </para>
/// </summary>
internal sealed class DetachProtocol(DebugEventBuffer buffer, ILogger logger)
{
	/// <summary>
	/// How many times a detach is attempted. <c>Stop</c>/<c>Detach</c> failing under contention is
	/// plausibly transient -- the case that found this was a detach immediately after a
	/// <c>Continue</c> that released a step hold -- so it is worth asking again.
	/// </summary>
	internal const int Attempts = 3;

	/// <summary>
	/// The wait between attempts. Taken between them rather than inside one, so the gate is released
	/// and a callback that is itself waiting on it can drain rather than being held off by the retry.
	/// </summary>
	internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

	/// <summary>
	/// How many stops a detach will take waiting for the target to come to rest with nothing on a
	/// breakpoint patch. Each round costs one continue and one stop, and a target whose breakpoint is
	/// hit faster than that never settles -- so the detach goes ahead rather than refusing, on the
	/// grounds that a debugger left attached is the worse of the two.
	/// </summary>
	private const int SettleRounds = 5;

	private long _windowStops;

	/// <summary>
	/// Counts a stop seen inside the detach window. A thread only gets onto a breakpoint patch by
	/// hitting one, so a stop-free round is the proof that removing the patches is safe.
	/// <para>
	/// Called from mscordbi's thread, without the gate, which the detaching thread is holding.
	/// </para>
	/// </summary>
	internal void CountStopInWindow() => Interlocked.Increment(ref _windowStops);

	/// <summary>
	/// Stops the target, and keeps stopping it until a round goes by with nothing hitting a breakpoint,
	/// so the patches can be removed with no thread part-way over one.
	/// <para>
	/// A stop on its own is not enough. The target runs between the continue that frees a held thread
	/// and the stop that takes it back, and a breakpoint hit in that gap puts another thread on a patch
	/// -- which is the crash this exists to prevent, and it shows up only under load, which is where
	/// the window is wide. A hit is the only way onto a patch, so a round with none is the proof.
	/// Giving up after <see cref="SettleRounds"/> detaches anyway: a target hit that often is rare,
	/// and a debugger left on somebody's application is the worse outcome of the two.
	/// </para>
	/// </summary>
	internal void Settle(CorDebugProcess process, int? pid)
	{
		for (var round = 1; ; round++)
		{
			var before = Interlocked.Read(ref _windowStops);

			// Detach needs a stopped process; stopping and detaching leaves the target running.
			process.Stop(0);

			if (Interlocked.Read(ref _windowStops) == before) return;

			if (round >= SettleRounds)
			{
				logger.LogWarning(
					"Detaching from pid {Pid} without the target coming to rest: it hit a breakpoint in each of "
						+ "{Rounds} rounds, so a thread may still be stepping over one.",
					pid,
					round);

				return;
			}

			// Something was hit while the target was running, so a thread may be part-way over a patch.
			// Let it run on, and take the stop again.
			process.Continue(fIsOutOfBand: false);
		}
	}

	/// <summary>
	/// Whether a failure is ICorDebug declining rather than faltering. It declines over anything the
	/// session still has live in the target, and declines identically however often it is asked, so
	/// retrying one of these costs the delay and reports the same failure three times over -- while
	/// the log says "attempt 1 of 3" about something that was never going to change.
	/// </summary>
	internal static bool IsRefusal(Exception? failure) => failure is DebugException debug && debug.HResult
		is HRESULT.CORDBG_E_DETACH_FAILED_OUTSTANDING_BREAKPOINTS
		or HRESULT.CORDBG_E_DETACH_FAILED_OUTSTANDING_STEPPERS
		or HRESULT.CORDBG_E_DETACH_FAILED_OUTSTANDING_EVALS
		or HRESULT.CORDBG_E_DETACH_FAILED_OUTSTANDING_TARGET_RESOURCES
		or HRESULT.CORDBG_E_DETACH_FAILED_ON_ENC;

	/// <summary>
	/// Records a detach that did not happen, and hands back the reason for the caller to return.
	/// <para>
	/// The failure deserves an event more than the success does: without one, a caller is told the
	/// session closed and is never told the debugger is still on their process. It also travels back
	/// as a string, because a caller detaching is closing the session and the event buffer goes with
	/// it.
	/// </para>
	/// </summary>
	internal string ReportFailure(Exception? error, int? pid)
	{
		var refused = IsRefusal(error);
		var effort = refused ? "it was refused" : $"after {Attempts} attempts";

		// Trimmed because the reason goes mid-sentence in every message that carries it, and an
		// exception's message usually ends with its own full stop.
		var failure = $"{effort}: {error?.Message.TrimEnd('.') ?? "no reason given"}";

		buffer.Append(
			LiveDebugEventKind.SessionNotice,
			$"Could not detach from pid {pid}, {failure}. The debugging interface is being left open "
				+ "rather than terminated, because terminating it while still attached kills the target.");

		logger.LogError(
			error,
			"Detach from pid {Pid} failed{Effort}.",
			pid,
			refused ? " and was not retried" : $" after {Attempts} attempts");

		return failure;
	}
}
