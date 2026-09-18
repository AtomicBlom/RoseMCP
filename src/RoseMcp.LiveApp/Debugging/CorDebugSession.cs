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
internal sealed class CorDebugSession(DebugEventBuffer buffer, ILogger logger) : IDisposable
{
	/// <summary>
	/// How long a manual pause waits for the runtime to reach a point it can be stopped at. Generous,
	/// because the whole reason somebody reaches for pause is an app that is busy or wedged -- and a
	/// bound rather than none, because a runtime that never gets there must not take the caller with it.
	/// </summary>
	private const int BreakTimeoutMilliseconds = 5000;

	/// <summary>How many frames a caller gets when it does not say. Enough to see how it got here.</summary>
	private const int DefaultFrameLimit = 50;

	/// <summary>
	/// How many of a value's children are reported. An array of a million elements is a real thing
	/// to stop on, and reading every element to answer one expander is not.
	/// </summary>
	private const int MaxChildren = 100;

	/// <summary>
	/// What every inspection answers with when the target is not held. Said rather than refused: a
	/// stop ends for reasons the caller did not cause, and an error would read as a broken call.
	/// </summary>
	private const string NotStoppedDetail =
		"The target is running. Frames, variables and threads can only be read while it is held at a "
			+ "breakpoint or a step.";

	/// <summary>
	/// The longest an operator's hold can suspend the safety timer for. A hold exists so a stack does
	/// not move while somebody reads it; a reader who walks away must not leave somebody's app frozen,
	/// which is the reason the safety timer exists in the first place.
	/// </summary>
	private const int MaxHoldSeconds = 600;

	/// <summary>What a hold lasts when the caller does not say. Long enough to read a stack and think.</summary>
	private const int DefaultHoldSeconds = 300;

	private readonly Lock _gate = new();
	// Reading a stopped target, which needs none of this session's state -- it is handed the process
	// and the held thread per call. Separate because holding a stop and reading one are different jobs.
	private readonly CorDebugInspector _inspector = new(logger);

	/// <summary>
	/// What a stop event says about where the target stopped. Read from the thread on the callback
	/// that announced it, because that is when the thread is known to be stopped.
	/// </summary>
	private readonly StopNarrative _narrative = new(logger);

	/// <summary>
	/// Steppers issued and not yet completed. ICorDebug refuses to detach while one is outstanding,
	/// so the session has to be able to find them again; a stepper handed to <c>Step</c> and dropped
	/// is unreachable and there is no way to ask the process for its list.
	/// </summary>
	private readonly List<CorDebugStepper> _steppers = [];

	/// <summary>
	/// Every module file the target has loaded, and what their metadata and symbols say.
	/// <para>
	/// Kept rather than asked for each time, because asking means synchronizing the target and a
	/// search runs on every keystroke of an autocomplete. What is inside a module is on disk, so once
	/// the path is known nothing else about the answer needs the debuggee at all.
	/// </para>
	/// </summary>
	private readonly TargetSymbols _symbols = new(logger);

	/// <summary>
	/// Every breakpoint and tracepoint asked for, bound or waiting for the module that would carry it.
	/// </summary>
	private readonly BreakpointTable _breakpoints = new(buffer, logger);

	/// <summary>
	/// The ICorDebug interface and the process it was got onto. Attaching is discovery rather than
	/// configuration -- which mscordbi talks to a target is decided by the coreclr that target runs --
	/// and it is the one part of a session that can fail before there is a session at all.
	/// </summary>
	private readonly RuntimeAttachment _runtime = new(buffer, logger);

	/// <summary>
	/// How a detach is attempted and what it says when it fails. The state transitions stay here; the
	/// counting and the deciding are its.
	/// </summary>
	private readonly DetachProtocol _detach = new(buffer, logger);

	/// <summary>
	/// What the target is doing, as one value rather than a set of flags that can disagree. Swapped
	/// under <see cref="_gate"/> everywhere except the two places mscordbi's callback thread has to
	/// see it while another thread holds the gate -- the detach window and an exit inside it -- which
	/// is why it moves through <see cref="Volatile"/> and <see cref="Interlocked"/> rather than plain
	/// assignment.
	/// </summary>
	private TargetExecution _execution = new TargetExecution.Running();

	public int? TargetProcessId => _runtime.ProcessId;

	/// <summary>The process being debugged, or null before an attach has succeeded.</summary>
	private CorDebugProcess? Debuggee => _runtime.Process;

	public bool HasExited => Execution is TargetExecution.Exited;

	/// <summary>
	/// What the target is doing. One read of one reference, so a caller cannot catch two halves of a
	/// transition -- and safe to ask for without the gate, which is what the callback thread needs.
	/// </summary>
	private TargetExecution Execution => Volatile.Read(ref _execution);

	/// <summary>Moves the target to a state, for the callers that know which one it is now in.</summary>
	private void MoveTo(TargetExecution next) => Volatile.Write(ref _execution, next);

	/// <summary>
	/// Ends the stop being held, standing down its timers, and hands back what it was -- or null when
	/// nothing was stopped. The target is left running, which is what it is about to be doing in every
	/// caller: each of them is the thing that resumes it.
	/// </summary>
	private StopRecord? EndStop()
	{
		var stop = Execution.Stop;
		stop?.Dispose();
		MoveTo(new TargetExecution.Running());
		return stop;
	}

	/// <summary>
	/// Attaches to a running process, waiting briefly for its runtime if it has only just started.
	/// Throws with a plain message when the target is not a debuggable .NET process.
	/// </summary>
	public void Attach(int pid, TimeSpan? runtimeReadyTimeout = null) =>
		_runtime.Attach(pid, runtimeReadyTimeout ?? RuntimeAttachment.RuntimeReadyTimeout, OnEvent);

	/// <summary>
	/// Launches an executable under the debugger and attaches at runtime startup, so the target is
	/// under debug from birth and its early events are captured.
	/// </summary>
	public void Launch(string executablePath, string? arguments) =>
		_runtime.Launch(executablePath, arguments, OnEvent);

	/// <summary>
	/// Attaches from birth to a UWP app that PLM has created suspended (issue #5): given the pid the
	/// resume stub reported and a resume action that releases the app's main thread.
	/// </summary>
	public void AttachUwpAtStartup(int pid, Action resume, TimeSpan startupTimeout) =>
		_runtime.AttachUwpAtStartup(pid, resume, startupTimeout, OnEvent);

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
		lock (_gate)
		{
			failure = null;

			// Nothing is attached, so what detaching promises -- the target keeps running, and the
			// interface is safe to terminate -- already holds.
			if (Debuggee is null || !Execution.IsLive) return true;

			// Read before the stop ends: nothing else records that the target was held, and whether it
			// was decides the first step below.
			var held = EndStop() is not null;

			try
			{
				// Between the continue and the stop the target runs with its breakpoints still live, so
				// an event can arrive; it is continued and counted without the gate, which this holds.
				var detaching = new TargetExecution.Detaching();
				MoveTo(detaching);
				try
				{
					// Off the patch first, while the breakpoint it is parked on is still there to step
					// over.
					if (held) Debuggee.Continue(fIsOutOfBand: false);

					_detach.Settle(Debuggee, TargetProcessId);
				}
				finally
				{
					// Narrow on purpose. Past the settling the target is synchronised, and what follows
					// is the detach itself -- where continuing a callback would be answering on behalf
					// of a process this session is letting go of.
					//
					// Compared rather than assigned: the target can have exited inside this window, and
					// a process that has gone outranks one that is running again.
					Interlocked.CompareExchange(ref _execution, new TargetExecution.Running(), detaching);
				}

				ReleaseForDetach();

				Debuggee.Detach();
				MoveTo(new TargetExecution.Detached());
				buffer.Append(LiveDebugEventKind.SessionNotice, "Detached; the target keeps running.");
				logger.LogInformation("Detached from pid {Pid} on attempt {Attempt}.", TargetProcessId, attempt);
				return true;
			}
			catch (Exception exception)
			{
				failure = exception;
				logger.LogWarning(
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
		lock (_gate)
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
		_breakpoints.ReleaseForDetach();

		foreach (var stepper in _steppers)
		{
			try
			{
				stepper.Deactivate();
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Deactivating a stepper before detaching failed.");
			}
		}

		_steppers.Clear();
	}

	/// <summary>
	/// Whether a detach failure is a refusal rather than contention. ICorDebug says no to detaching
	/// over outstanding breakpoints, steppers, evaluations, an edit-and-continue session or held target
	/// resources, and says it the same way however many times it is asked -- so retrying one of these
	/// turns a deterministic refusal into a slightly slower deterministic refusal, and calls it
	/// transient in the log while doing so.
	/// </summary>
	/// <summary>
	/// Adds a tracepoint: a breakpoint that logs and auto-continues, never pausing the target. It binds
	/// immediately if its module is already loaded and otherwise when the module loads.
	/// </summary>
	public LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit, string? condition)
	{
		if (logEveryNthHit is < 1) throw new ArgumentException("logEveryNthHit must be at least 1.");

		var binding = AddBinding(location, stopOnHit: false, logMessage, logEveryNthHit, autoContinueSeconds: null, condition);
		lock (_gate)
		{
			return BreakpointTable.DescribeTracepoint(binding);
		}
	}

	/// <summary>
	/// Adds a stopping breakpoint: on hit it holds the target and records the stop with its stack, then
	/// auto-continues after <paramref name="autoContinueSeconds"/> (default 30) so an unattended stop
	/// cannot wedge the app. Call <see cref="Continue"/> to resume sooner.
	/// </summary>
	public LiveBreakpoint AddBreakpoint(string location, int? autoContinueSeconds, string? condition)
	{
		if (autoContinueSeconds is < 1) throw new ArgumentException("autoContinueSeconds must be at least 1.");

		var binding = AddBinding(location, stopOnHit: true, logMessage: null, logEveryNthHit: null, autoContinueSeconds, condition);
		lock (_gate)
		{
			return BreakpointTable.DescribeBreakpoint(binding);
		}
	}

	public IReadOnlyList<LiveTracepoint> ListTracepoints()
	{
		lock (_gate)
		{
			return _breakpoints.Tracepoints();
		}
	}

	public IReadOnlyList<LiveBreakpoint> ListBreakpoints()
	{
		lock (_gate)
		{
			return _breakpoints.Breakpoints();
		}
	}

	/// <summary>
	/// Finds methods by name across the target's loaded modules, best first, for choosing somewhere
	/// to put a breakpoint without an IDE to browse.
	/// <para>
	/// The reading happens outside the session's lock and outside the debuggee. Which modules are
	/// loaded is the only thing the target is asked, and that is asked once; everything after it is
	/// metadata on disk. So this answers while the target is running, which is what an autocomplete
	/// needs -- and it answers while the target is wedged, which is when somebody most wants to set a
	/// breakpoint.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The limit is below one.</exception>
	public LiveMethodMatches SearchMethods(string? query, int limit)
	{
		// Before the module paths are asked for, because asking can stop the target to walk them and
		// a limit that cannot be honoured must not cost the debuggee a synchronization.
		if (limit < 1) throw new ArgumentException("limit must be at least 1.", nameof(limit));

		return TargetSymbols.Search(ModulePaths(), query, limit);
	}

	/// <summary>
	/// A method's source and every position inside it a breakpoint can be set at.
	/// <para>
	/// The positions cover the lambdas, local functions and state machines written inside the method
	/// as well as the method itself, because that is where the instructions for those lines actually
	/// live. Each carries the location that breaks there, so picking a line inside a lambda produces
	/// a breakpoint in the lambda without anybody having to know its name.
	/// </para>
	/// <para>
	/// Every way this can come up short -- no such module, no such method, no symbols, symbols from
	/// another build, a source file this machine never had -- is a sentence and an empty listing
	/// rather than a refusal, because a breakpoint at the method's first instruction is still
	/// available in all of them.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The location does not parse.</exception>
	public LiveMethodSource ReadMethodSource(string location) => TargetSymbols.ReadSource(ModulePaths(), location);

	public bool RemoveTracepoint(string id) => RemoveBinding(id);

	public bool RemoveBreakpoint(string id) => RemoveBinding(id);

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

		lock (_gate)
		{
			if (Debuggee is null || Execution.Stop is not { } stop)
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
				logger.LogWarning(exception, "Issuing a {Mode} step failed.", mode);
				return new LiveContinueResult { Continued = false };
			}

			var releasedHold = stop.IsHeld;

			// Resume so the step executes; the StepComplete callback holds the target again.
			EndStop();

			try
			{
				Debuggee.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Continuing for a step failed.");
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
	public LiveStop? CurrentStop()
	{
		lock (_gate)
		{
			return Execution.Stop?.Describe();
		}
	}

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

		lock (_gate)
		{
			if (Debuggee is not { } live || !Execution.IsLive)
			{
				return NotPaused("There is no live target to pause: the session has detached, or the process has gone.");
			}

			if (Execution is TargetExecution.Stopped) return NotPaused("The target is already stopped.");

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
			logger.LogWarning(exception, "Pausing pid {Pid} failed.", TargetProcessId);
			return NotPaused($"The target could not be paused: {exception.Message}");
		}

		// A breakpoint can have arrived while the stop was being taken, and it owns the stop it made.
		// Ours is then a second stop on the same process, which one continue would not undo -- so it
		// goes back, and the caller is told about the stop that is really there.
		lock (_gate)
		{
			if (Execution is TargetExecution.Stopped)
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

				if (_inspector.WalkFrames(thread).Frames.Count > 0) return thread;
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Choosing a thread to pause pid {Pid} on failed.", TargetProcessId);
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
			logger.LogWarning(exception, "Giving back an unused stop on pid {Pid} failed.", TargetProcessId);
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
		lock (_gate)
		{
			if (Execution.Stop is not { } held)
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
	/// now stands. Asking again extends the hold. Called with <c>_gate</c> held.
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

		buffer.Append(
			LiveDebugEventKind.SessionNotice,
			$"Held for an operator until {stop.HoldUntilUtc:HH:mm:ss}Z; the auto-continue timer is suspended "
				+ "until then or until something resumes the target.");

		return stop.Describe();
	}

	/// <summary>
	/// Gives a held stop back to the safety timer, re-armed with the interval its breakpoint asked
	/// for. Reports the stop as it now stands. Called with <c>_gate</c> held.
	/// </summary>
	private LiveStop ReleaseHold(StopRecord stop)
	{
		if (!stop.IsHeld) return stop.Describe();

		stop.ArmSafetyTimer(() => ContinueInternal(ResumeCause.SafetyTimer, stop));

		buffer.Append(
			LiveDebugEventKind.SessionNotice,
			$"The operator hold is released; the target auto-continues in {stop.AutoContinueSeconds}s.");

		return stop.Describe();
	}

	/// <summary>
	/// A page of a stopped thread's call stack. Reports the target as running rather than refusing
	/// when nothing is held, because a stop ends on its own and a caller polling one is not at fault.
	/// </summary>
	/// <param name="threadId">The thread to walk, or null for the one the debugger is holding.</param>
	/// <param name="offset">Where to start, zero being the innermost frame.</param>
	/// <param name="limit">How many frames to report, or null for a sensible page.</param>
	/// <exception cref="ArgumentException">The offset or limit is negative, or the thread is not there.</exception>
	public LiveStackFrames ReadFrames(int? threadId, int offset, int? limit)
	{
		if (offset < 0) throw new ArgumentException($"A frame offset cannot be negative; {offset} was asked for.");
		if (limit is < 1) throw new ArgumentException($"A frame limit has to be at least 1; {limit} was asked for.");

		lock (_gate)
		{
			if (CurrentStop() is not { } stop)
			{
				return new LiveStackFrames
				{
					Execution = LiveExecutionState.Running,
					Detail = NotStoppedDetail,
					ThreadId = threadId,
					Offset = offset,
					Total = 0,
					Truncated = false,
				};
			}

			return _inspector.Frames(Stopped(stop), threadId, offset, limit ?? DefaultFrameLimit);
		}
	}

	/// <summary>
	/// One frame's arguments and locals. The frame is named by its index in the stack this session
	/// would report now, so a caller reads a stack and then asks about a row of it.
	/// </summary>
	/// <exception cref="ArgumentException">The index is negative, the thread is not there, or there is no such frame.</exception>
	public LiveFrameVariables ReadFrameVariables(int frameIndex, int? threadId)
	{
		if (frameIndex < 0) throw new ArgumentException($"A frame index cannot be negative; {frameIndex} was asked for.");

		lock (_gate)
		{
			if (CurrentStop() is not { } stop)
			{
				return new LiveFrameVariables
				{
					Execution = LiveExecutionState.Running,
					Detail = NotStoppedDetail,
					FrameIndex = frameIndex,
					ThreadId = threadId,
					Symbols = LiveSymbolState.NoSymbols,
					Truncated = false,
				};
			}

			return _inspector.Variables(Stopped(stop), frameIndex, threadId);
		}
	}

	/// <summary>
	/// What is inside a value: an object's fields, or an array's elements, addressed by the same
	/// <see cref="LiveVariable.Path"/> the value was reported under.
	/// </summary>
	/// <exception cref="ArgumentException">The path does not parse, the frame is not there, or the path resolves to nothing.</exception>
	public LiveValueExpansion Expand(string path, int frameIndex, int? threadId)
	{
		// Parsed before the lock, because a path that does not parse is the caller's mistake and
		// there is no reason to hold the session to say so.
		var parsed = ValuePath.Parse(path);
		if (frameIndex < 0) throw new ArgumentException($"A frame index cannot be negative; {frameIndex} was asked for.");

		lock (_gate)
		{
			if (CurrentStop() is not { } stop)
			{
				return new LiveValueExpansion
				{
					Execution = LiveExecutionState.Running,
					Detail = NotStoppedDetail,
					Path = path,
					Total = 0,
					Truncated = false,
				};
			}

			return _inspector.Expand(Stopped(stop), parsed, path, frameIndex, threadId);
		}
	}

	/// <summary>
	/// Every managed thread of the stopped target, the held one first. Only while stopped: reading
	/// threads needs the runtime synchronized, and synchronizing it to answer would stop the app.
	/// </summary>
	public LiveThreadList ReadThreads()
	{
		lock (_gate)
		{
			if (CurrentStop() is not { } stop)
			{
				return new LiveThreadList { Execution = LiveExecutionState.Running, Detail = NotStoppedDetail };
			}

			return _inspector.Threads(Stopped(stop));
		}
	}

	/// <summary>
	/// Evaluates a field-access expression against the held frame. No debuggee code runs, so a
	/// property with a getter cannot be read and nothing the expression names can have a side effect.
	/// </summary>
	public LiveEvaluation Evaluate(string expression)
	{
		lock (_gate)
		{
			if (Debuggee is null || Execution.Stop is not { } stop)
			{
				return new LiveEvaluation { Expression = expression, Error = "The target is not stopped; evaluation needs a stop at a breakpoint or step." };
			}

			return _inspector.Evaluate(stop.Thread, expression);
		}
	}

	/// <summary>
	/// The stopped state, for handing to the inspector. Only correct while <c>_gate</c> is held and
	/// the target is stopped, which is why every caller above is inside the lock and past the guard.
	/// </summary>
	private StoppedTarget Stopped(LiveStop stop) => new(Debuggee!, Execution.Stop?.Thread, stop);

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
			logger.LogWarning(
				"Leaving the ICorDebug interface open for pid {Pid}: the detach failed, and terminating it "
					+ "while still attached would take the target down.",
				TargetProcessId);

			return;
		}

		_runtime.Terminate();
	}

	private BreakpointBinding AddBinding(string location, bool stopOnHit, string? logMessage, int? logEveryNthHit, int? autoContinueSeconds, string? condition)
	{
		BreakpointBinding binding;
		lock (_gate)
		{
			binding = _breakpoints.Add(location, stopOnHit, logMessage, logEveryNthHit, autoContinueSeconds, condition);
		}

		BindAgainstLoadedModules();
		return binding;
	}

	private bool RemoveBinding(string id)
	{
		lock (_gate)
		{
			return _breakpoints.Remove(id);
		}
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
		lock (_gate)
		{
			if (Debuggee is null || Execution.Stop is not { } stop)
			{
				return new LiveContinueResult { Continued = false };
			}

			var supersededTimer = armedFor is not null && !ReferenceEquals(armedFor, stop);
			var blockedByHold = cause == ResumeCause.SafetyTimer && stop.IsHeld;

			// Both are no-ops rather than refusals: nothing asked for them.
			if (supersededTimer || blockedByHold) return new LiveContinueResult { Continued = false };

			var releasedHold = cause == ResumeCause.Caller && stop.IsHeld;
			var id = stop.BindingId;

			EndStop();

			try
			{
				Debuggee.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Continuing from a stop failed.");
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

			buffer.Append(LiveDebugEventKind.SessionNotice, said);

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
		if (Execution is TargetExecution.Detaching)
		{
			if (e.Kind == CorDebugManagedCallbackKind.ExitProcess)
			{
				MoveTo(new TargetExecution.Exited());
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
				logger.LogDebug(exception, "Continue failed for a {Kind} arriving during a detach.", e.Kind);
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
			logger.LogDebug(exception, "A debug event handler failed for {Kind}.", e.Kind);
		}

		if (e.Kind == CorDebugManagedCallbackKind.ExitProcess)
		{
			// Under the gate, unlike the detach window above, because a target that goes while it is
			// being held has a stop to end: its timers would otherwise outlive the process, and a
			// reader would be told a dead target is stopped at a breakpoint. Nothing outside that
			// window holds the gate across a wait on mscordbi, so this cannot be the thread it needs.
			lock (_gate)
			{
				Execution.Stop?.Dispose();
				MoveTo(new TargetExecution.Exited());
			}

			return;
		}

		// A stopping breakpoint holds the target; Continue resumes it later.
		if (!shouldContinue) return;

		lock (_gate)
		{
			if (Execution is TargetExecution.Detached) return;

			try
			{
				e.Controller.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Continue failed after {Kind}.", e.Kind);
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
				buffer.Append(LiveDebugEventKind.ProcessCreated, $"Process {created.Process.Id} reported to the debugger.");
				return true;

			case LoadModuleCorDebugManagedCallbackEventArgs loaded:
				buffer.Append(LiveDebugEventKind.ModuleLoaded, $"Loaded {loaded.Module.Name}", moduleName: loaded.Module.Name);
				BindModule(loaded.Module);
				return true;

			case LogMessageCorDebugManagedCallbackEventArgs log:
				buffer.Append(
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
				buffer.Append(LiveDebugEventKind.ProcessExited, "The target process exited.");
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

		buffer.Append(
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
		lock (_gate)
		{
			(binding, ordinal) = _breakpoints.Match(hit.Breakpoint as CorDebugFunctionBreakpoint);
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
		var suffix = binding?.LogMessage is { Length: > 0 } message ? $": {message}" : string.Empty;
		buffer.Append(
			LiveDebugEventKind.BreakpointHit,
			$"Tracepoint {location} hit #{ordinal} on thread {threadId?.ToString() ?? "?"}{suffix}",
			threadId: threadId);
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

		lock (_gate)
		{
			// Whatever was held here is over, and its timers go with it: a new stop never inherits the
			// previous one's hold.
			Execution.Stop?.Dispose();

			var stop = new StopRecord(thread, bindingId, StateOf(kind), seconds);
			MoveTo(new TargetExecution.Stopped(stop));

			stop.EventSequence = buffer.Append(
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

	/// <summary>
	/// Binds any unbound bindings against modules already loaded when the binding was added. It
	/// async-breaks the target to a synchronized state to enumerate its modules, then resumes it; a
	/// binding whose module has not loaded yet stays unbound and binds later on the load callback.
	/// </summary>
	private void BindAgainstLoadedModules()
	{
		lock (_gate)
		{
			if (Debuggee is null || !Execution.IsLive) return;
			if (_breakpoints.AllBound) return;

			var stopped = false;
			try
			{
				Debuggee.Stop(0);
				stopped = true;

				var loaded = new List<CorDebugModule>();
				foreach (var module in TargetSymbols.EnumerateModules(Debuggee))
				{
					_symbols.Remember(module);
					loaded.Add(module);
				}

				// This walk is the one a name search would otherwise have to take for itself.
				_symbols.MarkWalked();

				_breakpoints.BindAmongLoaded(loaded);
				_breakpoints.ExplainUnbound(_symbols.Paths);
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Binding against loaded modules failed.");
			}
			finally
			{
				if (stopped)
				{
					try
					{
						Debuggee.Continue(fIsOutOfBand: false);
					}
					catch (Exception exception)
					{
						logger.LogDebug(exception, "Continue after bind failed.");
					}
				}
			}
		}
	}

	/// <summary>
	/// Binds one binding against modules that are loaded now. A location naming its assembly is tried
	/// against each module of that name. One naming none binds only where exactly one module declares
	/// the type, and when several do it says which rather than choosing: a breakpoint in the wrong one
	/// never fires, which looks exactly like code that never runs.
	/// </summary>
	/// <summary>
	/// The target's loaded module files, walking the ones that predate this session's attach the
	/// first time anybody asks. The walk is the only part that needs the gate or the debuggee; what
	/// a caller does with the paths afterwards is reading off disk.
	/// </summary>
	private IReadOnlyList<string> ModulePaths()
	{
		lock (_gate)
		{
			if (!_symbols.Walked && Debuggee is { } live && Execution.IsLive) _symbols.Walk(live);

			return _symbols.Paths;
		}
	}

	/// <summary>
	/// Takes in a module as it loads: notes its file, and binds anything waiting for a module that
	/// declares what it names. Called from a stopped callback.
	/// <para>
	/// A location without its assembly is where a second declaration of its type first becomes
	/// knowable, since the other modules are the ones already loaded. An unbound one is refused and told
	/// which modules declare it. A bound one is left where it is and the event stream says so, because
	/// deactivating a breakpoint that a thread may be parked on fail-fasts the target.
	/// </para>
	/// </summary>
	/// <summary>
	/// Takes in a module as it loads: notes its file, and binds anything waiting for it. Called from a
	/// stopped callback.
	/// </summary>
	private void BindModule(CorDebugModule module)
	{
		lock (_gate)
		{
			// Remembered before anything returns early, because the list of modules is wanted by a
			// name search whether or not anything is waiting to bind.
			_symbols.Remember(module);
			_breakpoints.BindNewModule(module, _symbols.Paths);
		}
	}

}

/// <summary>The process exists but its CoreCLR has not loaded yet; the caller may retry.</summary>
internal sealed class RuntimeNotReadyException(int pid)
	: Exception($"pid {pid} has no CoreCLR loaded yet.");
