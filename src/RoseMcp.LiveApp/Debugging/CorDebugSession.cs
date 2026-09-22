using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// One ICorDebug session against one process, attached to or launched. Callbacks arrive on mscordbi's
/// own thread with the debuggee stopped, and nothing in it runs again until <c>Continue</c> is called,
/// so every handler ends by recording the event and continuing -- except ExitProcess, after which
/// there is nothing left to continue, and a stopping breakpoint, which deliberately holds.
/// <para>
/// Nothing is injected into the target, so a running process can be watched as it is. It supports
/// tracepoints -- breakpoints that log and auto-continue, never pausing -- and stopping breakpoints,
/// which hold the target and notify, with a safety timeout so an unattended stop cannot wedge the app.
/// Events are captured into a <see cref="DebugEventBuffer"/> rather than printed, because the reader
/// is a turn-based agent that looks between its turns.
/// </para>
/// </summary>
internal sealed class CorDebugSession : IDisposable
{
	/// <summary>
	/// How long a manual pause waits for the runtime to reach a point it can be stopped at. Generous,
	/// because the whole reason somebody reaches for pause is an app that is busy or wedged -- and a
	/// bound rather than none, because a runtime that never gets there must not take the caller with it.
	/// </summary>
	private const int BreakTimeoutMilliseconds = 5000;

	/// <summary>
	/// The longest an operator's hold can suspend the safety timer for. A hold exists so a stack does
	/// not move while somebody reads it; a reader who walks away must not leave somebody's app frozen,
	/// which is the reason the safety timer exists in the first place.
	/// </summary>
	private const int MaxHoldSeconds = 600;

	/// <summary>What a hold lasts when the caller does not say. Long enough to read a stack and think.</summary>
	private const int DefaultHoldSeconds = 300;

	/// <summary>
	/// What a stop event says about where the target stopped. Read from the thread on the callback
	/// that announced it, because that is when the thread is known to be stopped.
	/// </summary>
	private readonly StopNarrative _narrative;

	/// <summary>
	/// Steppers issued and not yet completed. ICorDebug refuses to detach while one is outstanding,
	/// so the session has to be able to find them again; a stepper handed to <c>Step</c> and dropped
	/// is unreachable and there is no way to ask the process for its list.
	/// </summary>
	private readonly List<CorDebugStepper> _steppers = [];

	/// <summary>
	/// The process being debugged and what it is doing, with the lock that makes those one answer.
	/// Every verb below starts by asking it the same two things: whether there is still a target, and
	/// whether it is stopped. It owns every transition, so nothing here can move the target behind it.
	/// </summary>
	private readonly DebuggedTarget _target;

	/// <summary>
	/// How a detach is attempted and what it says when it fails. The state transitions stay here; the
	/// counting and the deciding are its.
	/// </summary>
	private readonly DetachProtocol _detach;

	/// <summary>
	/// The breakpoints and tracepoints set on this target, and the module metadata that says where
	/// each one goes. Reached through rather than wrapped, because it needs nothing from this session
	/// beyond the target it is set on.
	/// </summary>
	internal TargetBreakpoints Bindings { get; }

	/// <summary>
	/// Reading the target while it is held: frames, variables, threads and expressions. Reached
	/// through for the same reason as <see cref="Bindings"/> -- holding a stop and reading one are
	/// different jobs, and reading needs nothing from this session but the target.
	/// </summary>
	internal TargetInspection Inspection { get; }

	private readonly DebugEventBuffer _buffer;
	private readonly ILogger _logger;

	internal CorDebugSession(DebugEventBuffer buffer, ILogger logger)
	{
		_buffer = buffer;
		_logger = logger;
		_narrative = new StopNarrative(logger);
		_target = new DebuggedTarget(buffer, logger);
		_detach = new DetachProtocol(buffer, logger);
		Bindings = new TargetBreakpoints(_target, buffer, logger);
		Inspection = new TargetInspection(_target, logger);
	}

	public int? TargetProcessId => _target.ProcessId;

	public bool HasExited => _target.HasExited;

	/// <summary>
	/// Attaches to a running process, waiting briefly for its runtime if it has only just started.
	/// Throws with a plain message when the target is not a debuggable .NET process.
	/// </summary>
	public void Attach(int pid, TimeSpan? runtimeReadyTimeout = null) =>
		_target.Attach(pid, runtimeReadyTimeout ?? RuntimeAttachment.RuntimeReadyTimeout, OnEvent);

	/// <summary>
	/// Launches an executable under the debugger and attaches at runtime startup, so the target is
	/// under debug from birth and its early events are captured.
	/// </summary>
	public void Launch(string executablePath, string? arguments) =>
		_target.Launch(executablePath, arguments, OnEvent);

	/// <summary>
	/// Attaches from birth to a UWP app that PLM has created suspended (issue #5): given the pid the
	/// resume stub reported and a resume action that releases the app's main thread.
	/// </summary>
	public void AttachUwpAtStartup(int pid, Action resume, TimeSpan startupTimeout) =>
		_target.AttachUwpAtStartup(pid, resume, startupTimeout, OnEvent);

	/// <summary>
	/// Detaches from the target, leaving it running, and says whether it managed to.
	/// <para>
	/// The return value is the whole point. This used to log its own failure and return normally,
	/// which made the one operation whose entire contract is "the target keeps running" report
	/// success for a target it was about to kill: <see cref="Dispose"/> then called
	/// <c>Terminate()</c> under a comment asserting the process had been detached, and terminating
	/// the interface while still attached takes the debuggee down with it.
	/// </para>
	/// <para>
	/// Retried, and between attempts rather than inside one: <c>Stop</c>/<c>Detach</c> failing under
	/// contention is plausibly transient -- the case that found this was a detach immediately after
	/// a <c>Continue</c> that released a step hold -- and the gate is released between tries so a
	/// callback that is itself waiting on it can drain rather than being held off by the retry.
	/// </para>
	/// <para>
	/// A refusal is not contention and is not retried. ICorDebug declines to detach over anything the
	/// session still has bound in the target, and declines identically however often it is asked, so
	/// retrying one of those costs the delay and reports the same failure three times over -- while
	/// the log says "attempt 1 of 3" about something that was never going to change.
	/// </para>
	/// </summary>
	/// <param name="failure">
	/// Why the detach did not happen, when it did not: whether it was refused or retried, and the error
	/// it ended on. The event buffer is told the same thing, but a caller detaching is closing the session
	/// and the buffer goes with it, so the reason has to travel with the answer.
	/// </param>
	public bool Detach(out string? failure)
	{
		Exception? error = null;

		for (var attempt = 1; attempt <= DetachProtocol.Attempts; attempt++)
		{
			if (TryDetachOnce(attempt, out error))
			{
				failure = null;
				return true;
			}

			if (DetachProtocol.IsRefusal(error)) break;
			if (attempt < DetachProtocol.Attempts) Thread.Sleep(DetachProtocol.RetryDelay);
		}

		failure = _detach.ReportFailure(error, TargetProcessId);
		return false;
	}

	/// <summary>
	/// One attempt, holding the gate for no longer than the attempt itself so the caller can wait
	/// between tries without holding off the callbacks that arrive on mscordbi's thread.
	/// <para>
	/// Two facts about ICorDebug meet here and pull in opposite directions, and each was arrived at by
	/// a target dying. <c>Detach</c> refuses outright while any breakpoint is active, so they have to
	/// go first. And a thread parked on a patch that is then removed from under it fail-fasts the
	/// debuggee with 0xC0000409 -- while the detach reports success, so what a caller sees is a clean
	/// detach and a dead application. So a held thread is continued <em>before</em> anything is
	/// deactivated, and the patches only come out once the target is stopped with nothing on one.
	/// </para>
	/// </summary>
	private bool TryDetachOnce(int attempt, out Exception? failure)
	{
		lock (_target.Gate)
		{
			failure = null;

			// Nothing is attached, so what detaching promises -- the target keeps running, and the
			// interface is safe to terminate -- already holds.
			if (!_target.TryLive(out var process)) return true;

			// Read before the stop ends: nothing else records that the target was held, and whether it
			// was decides the first step below.
			var held = _target.EndStop() is not null;

			try
			{
				// Between the continue and the stop the target runs with its breakpoints still live, so
				// an event can arrive; it is continued and counted without the gate, which this holds.
				var window = _target.OpenDetachWindow();
				try
				{
					// Off the patch first, while the breakpoint it is parked on is still there to step
					// over.
					if (held) process.Continue(fIsOutOfBand: false);

					_detach.Settle(process, TargetProcessId);
				}
				finally
				{
					// Narrow on purpose. Past the settling the target is synchronised, and what follows
					// is the detach itself -- where continuing a callback would be answering on behalf
					// of a process this session is letting go of.
					_target.CloseDetachWindow(window);
				}

				ReleaseForDetach();

				process.Detach();
				_target.Detached();
				_buffer.Append(LiveDebugEventKind.SessionNotice, "Detached; the target keeps running.");
				_logger.LogInformation("Detached from pid {Pid} on attempt {Attempt}.", TargetProcessId, attempt);
				return true;
			}
			catch (Exception exception)
			{
				failure = exception;
				_logger.LogWarning(
					exception, "Detach from pid {Pid} failed on attempt {Attempt}.", TargetProcessId, attempt);
				return false;
			}
		}
	}

	/// <summary>
	/// Drops a stepper the runtime has finished with, so the outstanding list holds only steppers that
	/// really are outstanding. Deactivating a completed stepper is harmless, but keeping one means the
	/// detach path walks a list that grows with every step of the session.
	/// </summary>
	private void ForgetStepper(CorDebugStepper stepper)
	{
		lock (_target.Gate)
		{
			_steppers.Remove(stepper);
		}
	}

	/// <summary>
	/// Deactivates every breakpoint and stepper the session bound, which is ICorDebug's precondition
	/// for detaching: <c>Detach</c> refuses with CORDBG_E_DETACH_FAILED_OUTSTANDING_BREAKPOINTS or
	/// CORDBG_E_DETACH_FAILED_OUTSTANDING_STEPPERS while any of them is still active, so a session that
	/// did the one thing a debug session is for could not let go of the user's process.
	/// <para>
	/// Best effort and never throwing: a breakpoint that cannot be deactivated is worth trying to
	/// detach past, since the alternative is leaving the debugger attached to somebody's application.
	/// Called with the gate held and the process stopped.
	/// </para>
	/// </summary>
	private void ReleaseForDetach()
	{
		Bindings.ReleaseForDetach();

		foreach (var stepper in _steppers)
		{
			try
			{
				stepper.Deactivate();
			}
			catch (Exception exception)
			{
				_logger.LogDebug(exception, "Deactivating a stepper before detaching failed.");
			}
		}

		_steppers.Clear();
	}

	/// <summary>
	/// Resumes a target held at a breakpoint or a step, reporting whether anything was held and
	/// whether the resume released an operator's hold. Racing the safety timer is harmless: whichever
	/// arrives first clears the stop, and the loser finds nothing held.
	/// </summary>
	public LiveContinueResult Continue() => ContinueInternal(ResumeCause.Caller, armedFor: null);

	/// <summary>
	/// Steps the held thread: <c>in</c> into calls, <c>over</c> them, or <c>out</c> of the current
	/// frame. It resumes the target so the step runs; a StepComplete callback then holds it again at
	/// the new location. Reports not having continued when nothing is currently stopped, and refuses a
	/// mode that is none of the three rather than stepping over: a step moves the target, so treating a
	/// typo as the common case moves it somewhere nobody asked and reports success.
	/// <para>
	/// A step out of a held stop releases an operator's hold on it, the same as a resume, because the
	/// stop it was taken for is over. The stop the StepComplete callback creates is a new one and gets
	/// the safety timer again.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The mode is not in, over or out.</exception>
	public LiveContinueResult Step(string mode)
	{
		// Parsed before the lock and before the try below, which turns anything thrown inside it into
		// a plain "did not continue" -- and "nothing was stopped to step" is the one answer a caller
		// who mistyped the mode must not get.
		var direction = ArgumentValues.Step(mode);

		lock (_target.Gate)
		{
			if (!_target.TryHeld(out var process, out var stop))
			{
				return new LiveContinueResult { Continued = false };
			}

			try
			{
				var stepper = stop.Thread.CreateStepper();
				if (direction == StepDirection.Out) stepper.StepOut();
				else stepper.Step(bStepIn: direction == StepDirection.In);
				_steppers.Add(stepper);
			}
			catch (Exception exception)
			{
				_logger.LogWarning(exception, "Issuing a {Mode} step failed.", mode);
				return new LiveContinueResult { Continued = false };
			}

			var releasedHold = stop.IsHeld;

			// Resume so the step executes; the StepComplete callback holds the target again.
			_target.EndStop();

			try
			{
				process.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				_logger.LogWarning(exception, "Continuing for a step failed.");
				return new LiveContinueResult { Continued = false };
			}

			return new LiveContinueResult
			{
				Continued = true,
				Detail = releasedHold
					? "The stop this stepped out of was being held for an operator, so that hold is released. "
						+ "The stop the step lands on is a new one, under the safety timer again."
					: null,
			};
		}
	}

	/// <summary>
	/// What is asking a stop to end. It decides the sentence the event stream carries and whether a
	/// hold stands in the way, and those differ for all three -- a resume reported as a safety timeout
	/// sends a reader looking for a timer that did not fire.
	/// </summary>
	private enum ResumeCause
	{
		/// <summary>Somebody asked. Releases an operator's hold rather than being blocked by it.</summary>
		Caller,

		/// <summary>The safety timer. Does nothing while an operator holds the stop.</summary>
		SafetyTimer,

		/// <summary>The hold's own expiry, which is how a hold ends when nobody ends it.</summary>
		HoldExpiry,
	}

	/// <summary>
	/// The stop this session is holding, or null when the target is running. The one call a reader
	/// needs to know whether frames, locals and threads can be asked for at all.
	/// </summary>
	public LiveStop? CurrentStop() => _target.CurrentStop();

	/// <summary>
	/// Stops the target where it stands, rather than where a breakpoint would have put it.
	/// <para>
	/// The stop it makes is the same shape as a breakpoint's -- stack, top-frame variables, a safety
	/// timer -- because everything downstream reads a stop rather than reasoning about what made one.
	/// It is on the safety timer like any other, so a pause nobody comes back to frees the app.
	/// </para>
	/// </summary>
	/// <param name="autoContinueSeconds">How long an unattended pause lasts, or null for the default.</param>
	public LivePauseResult Break(int? autoContinueSeconds)
	{
		CorDebugProcess process;

		lock (_target.Gate)
		{
			if (!_target.TryLive(out var live))
			{
				return NotPaused("There is no live target to pause: the session has detached, or the process has gone.");
			}

			if (_target.IsStopped) return NotPaused("The target is already stopped.");

			process = live;
		}

		try
		{
			// Outside the gate: this waits on the runtime to reach a point it can be stopped at, and
			// holding the gate through it would block the very callbacks that get it there.
			process.Stop(BreakTimeoutMilliseconds);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Pausing pid {Pid} failed.", TargetProcessId);
			return NotPaused($"The target could not be paused: {exception.Message}");
		}

		// A breakpoint can have arrived while the stop was being taken, and it owns the stop it made.
		// Ours is then a second stop on the same process, which one continue would not undo -- so it
		// goes back, and the caller is told about the stop that is really there.
		lock (_target.Gate)
		{
			if (_target.IsStopped)
			{
				GiveBackStop(process);
				return NotPaused("The target stopped on its own before the pause took effect.");
			}
		}

		if (ThreadToPauseOn(process) is not { } thread)
		{
			GiveBackStop(process);
			return NotPaused("The target has no managed thread to stop on, so there is nothing a debugger could show.");
		}

		HoldAtStop(thread, LiveDebugEventKind.Paused, "Paused", bindingId: null, autoContinueSeconds);

		return new LivePauseResult
		{
			Execution = LiveExecutionState.PausedByOperator,
			Stop = CurrentStop(),
			Paused = true,
		};
	}

	/// <summary>
	/// The thread a manual pause reports itself on: the first with a managed frame, because a thread
	/// with none has nothing a stack pane could show and the runtime has plenty of them.
	/// </summary>
	private CorDebugThread? ThreadToPauseOn(CorDebugProcess process)
	{
		CorDebugThread? any = null;

		try
		{
			foreach (var thread in process.Threads)
			{
				any ??= thread;

				if (Inspection.HasManagedFrames(thread)) return thread;
			}
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Choosing a thread to pause pid {Pid} on failed.", TargetProcessId);
		}

		return any;
	}

	/// <summary>Undoes a stop this session took and then decided not to keep.</summary>
	private void GiveBackStop(CorDebugProcess process)
	{
		try
		{
			process.Continue(fIsOutOfBand: false);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Giving back an unused stop on pid {Pid} failed.", TargetProcessId);
		}
	}

	/// <summary>A pause that did not happen, and the stop that is there instead when there is one.</summary>
	private LivePauseResult NotPaused(string detail)
	{
		var stop = CurrentStop();

		return new LivePauseResult
		{
			Execution = stop?.State ?? LiveExecutionState.Running,
			Stop = stop,
			Paused = false,
			Detail = detail,
		};
	}

	/// <summary>What a stop of this kind is, said once rather than inferred from what is missing.</summary>
	private static LiveExecutionState StateOf(LiveDebugEventKind kind) => kind switch
	{
		LiveDebugEventKind.BreakpointHit => LiveExecutionState.StoppedAtBreakpoint,
		LiveDebugEventKind.Paused => LiveExecutionState.PausedByOperator,
		_ => LiveExecutionState.StoppedAtStep,
	};

	/// <summary>
	/// Takes or releases an operator's hold on the current stop, and reports what the stop now is.
	/// <para>
	/// One entry point for both directions because the answer is the same shape either way and the
	/// caller wants the stop back: a hold is only meaningful next to the deadline it moved.
	/// </para>
	/// </summary>
	/// <param name="requested">How long to hold for, or null for the default. Ignored on a release.</param>
	/// <param name="release">Give the stop back to the safety timer rather than holding it.</param>
	public LiveHoldResult OperatorHold(TimeSpan? requested, bool release)
	{
		lock (_target.Gate)
		{
			if (_target.Stop is not { } held)
			{
				return new LiveHoldResult
				{
					Execution = LiveExecutionState.Running,
					Applied = false,
					Detail = "Nothing is stopped, so there is no stop to hold. A hold suspends the safety timer on a "
						+ "target already held at a breakpoint or a step.",
				};
			}

			var wasHeld = held.IsHeld;
			var stop = release ? ReleaseHold(held) : SetHold(held, requested);

			return new LiveHoldResult
			{
				Execution = stop?.State ?? LiveExecutionState.Running,
				Stop = stop,
				Applied = release ? wasHeld : stop is not null,
				Detail = release && !wasHeld
					? "Nothing was holding this stop; it was already on the safety timer."
					: null,
			};
		}
	}

	/// <summary>
	/// Suspends the safety timer while somebody reads <paramref name="stop"/>, and reports it as it
	/// now stands. Asking again extends the hold. Called with the target's gate held.
	/// <para>
	/// Bounded at <see cref="MaxHoldSeconds"/> however long is asked for, and the bound is the point
	/// rather than a formality: the safety timer exists so an unattended stop cannot wedge somebody's
	/// app, and a hold with no limit would hand that failure back under another name.
	/// </para>
	/// </summary>
	private LiveStop SetHold(StopRecord stop, TimeSpan? requested)
	{
		var seconds = Math.Clamp(
			(int)Math.Round((requested ?? TimeSpan.FromSeconds(DefaultHoldSeconds)).TotalSeconds),
			1,
			MaxHoldSeconds);

		stop.HoldForOperator(TimeSpan.FromSeconds(seconds), () => ContinueInternal(ResumeCause.HoldExpiry, stop));

		_buffer.Append(
			LiveDebugEventKind.SessionNotice,
			$"Held for an operator until {stop.HoldUntilUtc:HH:mm:ss}Z; the auto-continue timer is suspended "
				+ "until then or until something resumes the target.");

		return stop.Describe();
	}

	/// <summary>
	/// Gives a held stop back to the safety timer, re-armed with the interval its breakpoint asked
	/// for. Reports the stop as it now stands. Called with the target's gate held.
	/// </summary>
	private LiveStop ReleaseHold(StopRecord stop)
	{
		if (!stop.IsHeld) return stop.Describe();

		stop.ArmSafetyTimer(() => ContinueInternal(ResumeCause.SafetyTimer, stop));

		_buffer.Append(
			LiveDebugEventKind.SessionNotice,
			$"The operator hold is released; the target auto-continues in {stop.AutoContinueSeconds}s.");

		return stop.Describe();
	}

	/// <summary>
	/// Detaches, and terminates the debugging interface only if that worked.
	/// <para>
	/// <c>Terminate()</c>'s documented precondition is that every process has been detached from or
	/// terminated; running it while still attached is what takes the debuggee down with it. That
	/// precondition is checked rather than asserted in a comment: this detaches first and terminates
	/// only if that reported success, because the state saying so is the only evidence there is.
	/// </para>
	/// <para>
	/// Leaking the interface for the few seconds until this host process exits is strictly better
	/// than killing the application somebody is running.
	/// </para>
	/// </summary>
	public void Dispose()
	{
		if (!Detach(out _))
		{
			_logger.LogWarning(
				"Leaving the ICorDebug interface open for pid {Pid}: the detach failed, and terminating it "
					+ "while still attached would take the target down.",
				TargetProcessId);

			return;
		}

		_target.Terminate();
	}

	/// <summary>
	/// Resumes a held target. <paramref name="armedFor"/> is the stop a timer was armed for, and is
	/// null when a caller asked.
	/// <para>
	/// Two things separate a timer's resume from a caller's. A timer belonging to a stop that is
	/// already over does nothing -- disposing a timer does not wait for a callback already running, so
	/// comparing the record is what makes a hold safe rather than nearly safe. And the safety timer
	/// must not fire while a person is holding the stop, which is the whole point of a hold; the
	/// hold's own expiry is exempt from that, since it is the thing the hold ends with.
	/// </para>
	/// </summary>
	private LiveContinueResult ContinueInternal(ResumeCause cause, StopRecord? armedFor)
	{
		lock (_target.Gate)
		{
			if (!_target.TryHeld(out var process, out var stop))
			{
				return new LiveContinueResult { Continued = false };
			}

			var supersededTimer = armedFor is not null && !ReferenceEquals(armedFor, stop);
			var blockedByHold = cause == ResumeCause.SafetyTimer && stop.IsHeld;

			// Both are no-ops rather than refusals: nothing asked for them.
			if (supersededTimer || blockedByHold) return new LiveContinueResult { Continued = false };

			var releasedHold = cause == ResumeCause.Caller && stop.IsHeld;
			var id = stop.BindingId;

			_target.EndStop();

			try
			{
				process.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				_logger.LogWarning(exception, "Continuing from a stop failed.");
				return new LiveContinueResult { Continued = false };
			}

			var where = id is null ? "a step" : $"breakpoint {id}";
			var said = cause switch
			{
				ResumeCause.SafetyTimer => $"Auto-continued from {where} after the safety timeout.",
				ResumeCause.HoldExpiry => $"Auto-continued from {where}: the operator hold on it expired.",
				_ => $"Continued from {where}."
					+ (releasedHold ? " The operator hold on it is released." : string.Empty),
			};

			_buffer.Append(LiveDebugEventKind.SessionNotice, said);

			return new LiveContinueResult
			{
				Continued = true,
				Detail = releasedHold
					? "The target was being held for an operator. It has resumed and the hold is released, so "
						+ "anything reading that stop sees it end."
					: null,
			};
		}
	}

	private void OnEvent(object? sender, CorDebugManagedCallbackEventArgs e)
	{
		// A detach in progress lets the target run for an instant with its breakpoints still live, to
		// step a held thread off its patch, so an event can land here in that window. It must be
		// continued without taking the gate: the detach is holding it, and this is the thread mscordbi
		// needs back before its Stop can complete -- so waiting would stop the detach and the debuggee
		// both, permanently, which is the wedge the whole detach path exists to avoid.
		if (_target.IsDetaching)
		{
			if (e.Kind == CorDebugManagedCallbackKind.ExitProcess)
			{
				_target.ExitedWhileDetaching();
				return;
			}

			// Counted before it is continued, so the detaching thread cannot stop and read the count
			// between the two and conclude the target was quiet. Only the kinds that put a thread on a
			// patch count; a module load is not a reason to wait.
			var parks = e.Kind is CorDebugManagedCallbackKind.Breakpoint or CorDebugManagedCallbackKind.StepComplete;
			if (parks) _detach.CountStopInWindow();

			try
			{
				e.Controller.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				_logger.LogDebug(exception, "Continue failed for a {Kind} arriving during a detach.", e.Kind);
			}

			return;
		}

		var shouldContinue = true;
		try
		{
			shouldContinue = Record(e);
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "A debug event handler failed for {Kind}.", e.Kind);
		}

		if (e.Kind == CorDebugManagedCallbackKind.ExitProcess)
		{
			// Takes the gate, unlike the detach window above, because a target that goes while it is
			// being held has a stop to end. Nothing outside that window holds the gate across a wait
			// on mscordbi, so this cannot be the thread it needs.
			_target.Exited();
			return;
		}

		// A stopping breakpoint holds the target; Continue resumes it later.
		if (!shouldContinue) return;

		lock (_target.Gate)
		{
			if (_target.IsDetached) return;

			try
			{
				e.Controller.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				_logger.LogDebug(exception, "Continue failed after {Kind}.", e.Kind);
			}
		}
	}

	/// <summary>Records an event; returns whether the caller should continue the target afterwards.</summary>
	private bool Record(CorDebugManagedCallbackEventArgs e)
	{
		switch (e)
		{
			case CreateProcessCorDebugManagedCallbackEventArgs created:
				// Debugger.Log in the debuggee only produces LogMessage events once this is on.
				created.Process.EnableLogMessages(true);
				_buffer.Append(LiveDebugEventKind.ProcessCreated, $"Process {created.Process.Id} reported to the debugger.");
				return true;

			case LoadModuleCorDebugManagedCallbackEventArgs loaded:
				_buffer.Append(LiveDebugEventKind.ModuleLoaded, $"Loaded {loaded.Module.Name}", moduleName: loaded.Module.Name);
				Bindings.BindModule(loaded.Module);
				return true;

			case LogMessageCorDebugManagedCallbackEventArgs log:
				_buffer.Append(
					LiveDebugEventKind.LogMessage,
					$"[{log.LogSwitchName}] {log.Message.TrimEnd()}",
					threadId: CorDebugInspector.TryThreadId(log.Thread));
				return true;

			case BreakpointCorDebugManagedCallbackEventArgs hit:
				return RecordBreakpointHit(hit);

			case StepCompleteCorDebugManagedCallbackEventArgs step:
				ForgetStepper(step.Stepper);
				return HoldAtStop(step.Thread, LiveDebugEventKind.StepComplete, "Step complete", bindingId: null, autoContinueSeconds: null);

			case Exception2CorDebugManagedCallbackEventArgs exception:
				RecordException(exception);
				return true;

			case ExitProcessCorDebugManagedCallbackEventArgs:
				_buffer.Append(LiveDebugEventKind.ProcessExited, "The target process exited.");
				return true;

			// Thread churn and the rest are continued but not buffered: high volume, low signal.
			default:
				return true;
		}
	}

	private void RecordException(Exception2CorDebugManagedCallbackEventArgs exception)
	{
		// Catch-handler-found is bookkeeping that follows a first-chance throw; skip it as noise.
		if (exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND) return;

		var unhandled = exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED;
		var kind = unhandled ? LiveDebugEventKind.ExceptionUnhandled : LiveDebugEventKind.ExceptionFirstChance;
		var typeName = StopNarrative.ExceptionType(exception.Thread);

		// The thread is stopped in this callback, so this is the moment its stack can be walked.
		var frames = _narrative.Frames(exception.Thread);

		_buffer.Append(
			kind,
			$"{(unhandled ? "Unhandled" : "First-chance")} {typeName} on thread {CorDebugInspector.TryThreadId(exception.Thread)?.ToString() ?? "?"}",
			threadId: CorDebugInspector.TryThreadId(exception.Thread),
			exceptionType: typeName,
			frames: frames.Count > 0 ? frames : null);
	}

	private bool RecordBreakpointHit(BreakpointCorDebugManagedCallbackEventArgs hit)
	{
		var threadId = CorDebugInspector.TryThreadId(hit.Thread);

		BreakpointBinding? binding;
		long ordinal;
		lock (_target.Gate)
		{
			(binding, ordinal) = Bindings.Match(hit.Breakpoint as CorDebugFunctionBreakpoint);
		}

		// A condition is a cheap read-and-compare on the stopped frame; if it fails, act as if unhit.
		if (binding?.Condition is { } condition && !condition.Evaluate(_narrative.TopFrameVariables(hit.Thread)))
		{
			return true;
		}

		if (binding is { StopOnHit: true })
		{
			return HoldAtStop(hit.Thread, LiveDebugEventKind.BreakpointHit, $"Breakpoint {binding.Raw} hit #{ordinal}", binding.Id, binding.AutoContinueSeconds);
		}

		// Tracepoint: a hit-count filter still counts every hit; it only thins what is logged.
		if (binding?.LogEveryNthHit is { } nth && ordinal % nth != 0) return true;

		var location = binding?.Raw ?? "unknown location";

		// Rendered after the filters, not before: reading the frame is the expensive part of a hit,
		// and a hit that is conditioned out or thinned away never needed its values.
		var logged = binding?.LogTemplate is { } template ? _narrative.Interpolate(hit.Thread, template) : null;
		var suffix = logged?.Text is { Length: > 0 } message ? $": {message}" : string.Empty;

		_buffer.Append(
			LiveDebugEventKind.BreakpointHit,
			$"Tracepoint {location} hit #{ordinal} on thread {threadId?.ToString() ?? "?"}{suffix}",
			threadId: threadId,
			logged: logged?.Values is { Count: > 0 } values ? values : null);
		return true;
	}

	/// <summary>
	/// Holds the target stopped (a breakpoint or a completed step): records the stop with its stack and
	/// top-frame variables and arms the auto-continue safety timer. Returns false so the caller does not
	/// continue -- the target stays stopped until <see cref="Continue"/>, <see cref="Step"/>, or the
	/// timer fires. The walk happens before the lock because the thread is already stopped.
	/// <para>
	/// The event is appended before the timer is armed, because the sequence it comes back with is the
	/// stop's identity and the fields describing the stop are not complete without it.
	/// </para>
	/// </summary>
	private bool HoldAtStop(CorDebugThread thread, LiveDebugEventKind kind, string prefix, string? bindingId, int? autoContinueSeconds)
	{
		var frames = _narrative.Frames(thread);
		var variables = _narrative.TopFrameVariables(thread);
		var threadId = CorDebugInspector.TryThreadId(thread);
		var top = frames.Count > 0 ? frames[0] : "?";
		var seconds = autoContinueSeconds ?? StopRecord.DefaultAutoContinueSeconds;

		lock (_target.Gate)
		{
			var stop = new StopRecord(thread, bindingId, StateOf(kind), seconds);
			_target.TakeStop(stop);

			stop.EventSequence = _buffer.Append(
				kind,
				$"{prefix} at {top} on thread {threadId?.ToString() ?? "?"} -- stopped; continue or step (auto-continues in {seconds}s).",
				threadId: threadId,
				frames: frames.Count > 0 ? frames : null,
				variables: variables.Count > 0 ? variables : null);

			// Armed last, so a timer that fires the instant it is set cannot find a half-built stop. It
			// carries the record it belongs to, so a tick queued against a stop already over does nothing.
			stop.ArmSafetyTimer(() => ContinueInternal(ResumeCause.SafetyTimer, stop));
		}

		return false;
	}

}

/// <summary>The process exists but its CoreCLR has not loaded yet; the caller may retry.</summary>
internal sealed class RuntimeNotReadyException(int pid)
	: Exception($"pid {pid} has no CoreCLR loaded yet.");
