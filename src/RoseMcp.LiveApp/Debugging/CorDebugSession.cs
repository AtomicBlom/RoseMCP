using System.Runtime.InteropServices;

using ClrDebug;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

using RoseMcp.Contracts;
using RoseMcp.Symbols;

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
	private static readonly TimeSpan RuntimeReadyTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
	private const int MaxStackFrames = 20;
	private const int MaxVariables = 64;
	private const int DefaultAutoContinueSeconds = 30;

	/// <summary>
	/// The longest an operator's hold can suspend the safety timer for. A hold exists so a stack does
	/// not move while somebody reads it; a reader who walks away must not leave somebody's app frozen,
	/// which is the reason the safety timer exists in the first place.
	/// </summary>
	private const int MaxHoldSeconds = 600;

	/// <summary>What a hold lasts when the caller does not say. Long enough to read a stack and think.</summary>
	private const int DefaultHoldSeconds = 300;

	/// <summary>
	/// How many times to try detaching before giving up. Failure under contention is plausibly
	/// transient and a retry is far cheaper than what failing costs the person whose app it is.
	/// </summary>
	private const int DetachAttempts = 3;

	private static readonly TimeSpan DetachRetryDelay = TimeSpan.FromMilliseconds(50);

	private readonly Lock _gate = new();
	private readonly List<BreakpointBinding> _bindings = [];
	private int _nextBindingId = 1;

	private DbgShim? _shim;
	private CorDebug? _corDebug;
	private CorDebugProcess? _process;
	private bool _detached;
	private volatile bool _exited;

	private bool _stoppedAtBreakpoint;
	private string? _stoppedBindingId;
	private CorDebugThread? _stoppedThread;
	private Timer? _autoContinueTimer;

	// The sequence of the event that announced the current stop, and when it happened. Together they
	// are the stop's identity: two hits of one breakpoint on one thread are otherwise identical, so a
	// reader polling this session could not tell a new stop from the same one seen again -- and that
	// is exactly what decides whether frames and values have to be read afresh.
	private long _stopEventSequence;

	private DateTime _stoppedAtUtc;

	// What the current stop's safety timeout was set to, kept so releasing a hold can re-arm the timer
	// with the interval the breakpoint asked for rather than the default.
	private int _autoContinueSeconds;

	private DateTime _autoContinueAtUtc;

	// When an operator's hold expires, or null when nothing is holding this stop. A hold suspends the
	// safety timer so a person can read a stack without it moving under them; it is bounded because a
	// reader who walks away must not leave somebody's app frozen indefinitely.
	private DateTime? _holdUntilUtc;

	private Timer? _holdTimer;

	// Incremented on every stop, and captured by both timers. Timer.Dispose does not wait for a
	// callback already running, so a tick queued before a hold was taken would otherwise resume the
	// target under the person reading it. A callback whose generation has moved returns without
	// touching anything.
	private long _stopGeneration;

	public int? TargetProcessId { get; private set; }

	public bool HasExited => _exited;

	public bool IsStoppedAtBreakpoint
	{
		get
		{
			lock (_gate)
			{
				return _stoppedAtBreakpoint;
			}
		}
	}

	/// <summary>
	/// Attaches to a running process, waiting briefly for its runtime if it has only just started.
	/// Throws with a plain message when the target is not a debuggable .NET process.
	/// </summary>
	public void Attach(int pid, TimeSpan? runtimeReadyTimeout = null)
	{
		var shim = LoadDbgShim();
		var runtime = FindRuntimeWithRetry(shim, pid, runtimeReadyTimeout ?? RuntimeReadyTimeout);
		try
		{
			_corDebug = CreateCorDebug(shim, pid, runtime.Path);
			_process = _corDebug.DebugActiveProcess(pid, win32Attach: false);
			TargetProcessId = pid;
			buffer.Append(LiveDebugEventKind.SessionNotice, $"Attached to pid {pid} ({runtime.Path}).");
			logger.LogInformation("Attached to pid {Pid} ({Runtime}).", pid, runtime.Path);
		}
		finally
		{
			// The enumeration's handles are the runtimes' continue events; attach does not need them.
			shim.CloseCLREnumeration(runtime.Enumeration);
		}
	}

	/// <summary>
	/// Launches an executable under the debugger and attaches at runtime startup, so the target is
	/// under debug from birth and its early events are captured. The runtime is created suspended,
	/// resumed to the point it signals startup, attached to, then released.
	/// </summary>
	public void Launch(string executablePath, string? arguments)
	{
		var shim = LoadDbgShim();
		var commandLine = string.IsNullOrWhiteSpace(arguments) ? $"\"{executablePath}\"" : $"\"{executablePath}\" {arguments}";
		var workingDirectory = Path.GetDirectoryName(executablePath);
		var launched = shim.CreateProcessForLaunch(commandLine, bSuspendProcess: true, IntPtr.Zero, workingDirectory);
		try
		{
			AttachAtSuspendedStartup(shim, launched.ProcessId, () => shim.ResumeProcess(launched.ResumeHandle), StartupTimeout);
		}
		finally
		{
			shim.CloseResumeHandle(launched.ResumeHandle);
		}
	}

	/// <summary>
	/// Attaches from birth to a UWP app that PLM has created suspended (issue #5): given the pid the
	/// resume stub reported and a resume action that releases the app's main thread, it arms the
	/// runtime-startup notification before the resume, then attaches when the runtime signals. This is
	/// <see cref="Launch"/>'s mechanism for a process the shell created rather than dbgshim.
	/// </summary>
	public void AttachUwpAtStartup(int pid, Action resume, TimeSpan startupTimeout)
	{
		var shim = LoadDbgShim();
		AttachAtSuspendedStartup(shim, pid, resume, startupTimeout);
	}

	/// <summary>
	/// The shared startup-attach dance: arm the runtime-startup notification while the process is still
	/// suspended before its CLR has loaded, trigger the caller's <paramref name="resume"/>, wait for the
	/// runtime to signal, attach to it, and release it. The ordering is the whole trick -- the
	/// notification must be armed before the process is resumed, or the runtime can start before the
	/// debugger is listening and the startup is missed.
	/// </summary>
	private void AttachAtSuspendedStartup(DbgShim shim, int pid, Action resume, TimeSpan startupTimeout)
	{
		using var startup = WrapEvent(shim.GetStartupNotificationEvent(pid), ownsHandle: true);
		resume();
		buffer.Append(LiveDebugEventKind.SessionNotice, $"Resumed pid {pid}; waiting for its runtime.");

		if (!startup.WaitOne(startupTimeout))
		{
			// The modules the process has loaded say which runtime it is hosting, and that turns this
			// from a question into a diagnosis. "Is it a .NET (Core) app?" was accurate about the
			// process it got and useless: the answer was already in the process, and finding it meant
			// listing modules by hand and recognising mrt100_app.dll.
			var flavour = RuntimeFlavour.Describe(pid);

			throw new TimeoutException(flavour is null
				? "The process never signalled runtime startup, and its loaded modules could not be read to "
					+ "say why. Is it a .NET (Core) app?"
				: $"The process never signalled runtime startup. {flavour}");
		}

		var runtime = FindRuntimeWithRetry(shim, pid, RuntimeReadyTimeout);
		try
		{
			_corDebug = CreateCorDebug(shim, pid, runtime.Path);
			_process = _corDebug.DebugActiveProcess(pid, win32Attach: false);
			TargetProcessId = pid;

			// The runtime is parked on this event until the debugger says go.
			using var continueStartup = WrapEvent(runtime.Handle, ownsHandle: false);
			continueStartup.Set();

			buffer.Append(LiveDebugEventKind.SessionNotice, $"Attached to pid {pid} at startup.");
			logger.LogInformation("Attached at startup to pid {Pid} ({Runtime}).", pid, runtime.Path);
		}
		finally
		{
			shim.CloseCLREnumeration(runtime.Enumeration);
		}
	}

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
	/// </summary>
	public bool Detach()
	{
		Exception? failure = null;

		for (var attempt = 1; attempt <= DetachAttempts; attempt++)
		{
			if (TryDetachOnce(attempt, out failure)) return true;
			if (attempt < DetachAttempts) Thread.Sleep(DetachRetryDelay);
		}

		// The failure deserves an event more than the success does: without one, a caller is told the
		// session closed and is never told the debugger is still on their process.
		buffer.Append(
			LiveDebugEventKind.SessionNotice,
			$"Could not detach from pid {TargetProcessId} after {DetachAttempts} attempts: "
				+ $"{failure?.Message ?? "no reason given"}. The debugging interface is being left open rather "
				+ "than terminated, because terminating it while still attached kills the target.");

		logger.LogError(failure, "Detach from pid {Pid} failed after {Attempts} attempts.", TargetProcessId, DetachAttempts);

		return false;
	}

	/// <summary>
	/// One attempt, holding the gate for no longer than the attempt itself so the caller can wait
	/// between tries without holding off the callbacks that arrive on mscordbi's thread.
	/// </summary>
	private bool TryDetachOnce(int attempt, out Exception? failure)
	{
		lock (_gate)
		{
			failure = null;

			// Nothing is attached, so what detaching promises -- the target keeps running, and the
			// interface is safe to terminate -- already holds.
			if (_process is null || _detached || _exited) return true;

			ClearStopTimers();
			_stoppedAtBreakpoint = false;
			_stoppedThread = null;

			try
			{
				// Detach needs a stopped process; stopping and detaching leaves the target running.
				_process.Stop(0);
				_process.Detach();
				_detached = true;
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
	/// Adds a tracepoint: a breakpoint that logs and auto-continues, never pausing the target. It binds
	/// immediately if its module is already loaded and otherwise when the module loads.
	/// </summary>
	public LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit, string? condition)
	{
		if (logEveryNthHit is < 1) throw new ArgumentException("logEveryNthHit must be at least 1.");

		var binding = AddBinding(location, stopOnHit: false, logMessage, logEveryNthHit, autoContinueSeconds: null, condition);
		lock (_gate)
		{
			return DescribeTracepoint(binding);
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
			return DescribeBreakpoint(binding);
		}
	}

	public IReadOnlyList<LiveTracepoint> ListTracepoints()
	{
		lock (_gate)
		{
			return [.. _bindings.Where(binding => !binding.StopOnHit).Select(DescribeTracepoint)];
		}
	}

	public IReadOnlyList<LiveBreakpoint> ListBreakpoints()
	{
		lock (_gate)
		{
			return [.. _bindings.Where(binding => binding.StopOnHit).Select(DescribeBreakpoint)];
		}
	}

	public bool RemoveTracepoint(string id) => RemoveBinding(id);

	public bool RemoveBreakpoint(string id) => RemoveBinding(id);

	/// <summary>
	/// Resumes a target held at a breakpoint or a step, reporting whether anything was held and
	/// whether the resume released an operator's hold. Racing the safety timer is harmless: whichever
	/// arrives first clears the stop, and the loser finds nothing held.
	/// </summary>
	public LiveContinueResult Continue() => ContinueInternal(ResumeCause.Caller, generation: null);

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
			if (!_stoppedAtBreakpoint || _stoppedThread is null || _process is null || _detached || _exited)
			{
				return new LiveContinueResult { Continued = false };
			}

			try
			{
				var stepper = _stoppedThread.CreateStepper();
				if (direction == StepDirection.Out) stepper.StepOut();
				else stepper.Step(bStepIn: direction == StepDirection.In);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Issuing a {Mode} step failed.", mode);
				return new LiveContinueResult { Continued = false };
			}

			var releasedHold = _holdUntilUtc is not null;

			// Resume so the step executes; the StepComplete callback holds the target again.
			_stoppedAtBreakpoint = false;
			_stoppedBindingId = null;
			_stoppedThread = null;
			ClearStopTimers();

			try
			{
				_process.Continue(fIsOutOfBand: false);
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
			if (!_stoppedAtBreakpoint) return null;

			return new LiveStop
			{
				// A stop with no binding is a completed step: nothing else reaches Hold without one.
				State = _stoppedBindingId is null
					? LiveExecutionState.StoppedAtStep
					: LiveExecutionState.StoppedAtBreakpoint,
				ThreadId = _stoppedThread is null ? null : TryThreadId(_stoppedThread),
				BreakpointId = _stoppedBindingId,
				EventSequence = _stopEventSequence,
				StoppedAtUtc = _stoppedAtUtc,
				Resume = _holdUntilUtc is null ? LiveStopResume.AutoContinue : LiveStopResume.HeldByOperator,
				ResumeDeadlineUtc = _holdUntilUtc ?? _autoContinueAtUtc,
			};
		}
	}

	/// <summary>
	/// Suspends the safety timer while somebody reads this stop, and reports the stop as it now
	/// stands -- or null when there is nothing stopped to hold. Asking again extends it.
	/// <para>
	/// Bounded at <see cref="MaxHoldSeconds"/> however long is asked for, and the bound is the point
	/// rather than a formality: the safety timer exists so an unattended stop cannot wedge somebody's
	/// app, and a hold with no limit would hand that failure back under another name.
	/// </para>
	/// </summary>
	public LiveStop? SetHold(TimeSpan? requested)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint) return null;

			var seconds = Math.Clamp(
				(int)Math.Round((requested ?? TimeSpan.FromSeconds(DefaultHoldSeconds)).TotalSeconds),
				1,
				MaxHoldSeconds);

			var generation = _stopGeneration;

			// The safety timer goes rather than being left to fire into a guard, so there is one timer
			// per stop and the deadline the caller is told is the one that will actually arrive.
			_autoContinueTimer?.Dispose();
			_autoContinueTimer = null;
			_holdTimer?.Dispose();

			_holdUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
			_holdTimer = new Timer(
				_ => ContinueInternal(ResumeCause.HoldExpiry, generation),
				null,
				TimeSpan.FromSeconds(seconds),
				Timeout.InfiniteTimeSpan);

			buffer.Append(
				LiveDebugEventKind.SessionNotice,
				$"Held for an operator until {_holdUntilUtc:HH:mm:ss}Z; the auto-continue timer is suspended "
					+ "until then or until something resumes the target.");

			return CurrentStop();
		}
	}

	/// <summary>
	/// Gives a held stop back to the safety timer, re-armed with the interval its breakpoint asked
	/// for. Reports the stop as it now stands, or null when nothing is stopped.
	/// </summary>
	public LiveStop? ReleaseHold()
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint) return null;
			if (_holdUntilUtc is null) return CurrentStop();

			var generation = _stopGeneration;

			_holdTimer?.Dispose();
			_holdTimer = null;
			_holdUntilUtc = null;

			// From now rather than from the stop, because the interval is how long an unattended stop
			// may last and the stop has just stopped being unattended.
			_autoContinueAtUtc = DateTime.UtcNow.AddSeconds(_autoContinueSeconds);
			_autoContinueTimer?.Dispose();
			_autoContinueTimer = new Timer(
				_ => ContinueInternal(ResumeCause.SafetyTimer, generation),
				null,
				TimeSpan.FromSeconds(_autoContinueSeconds),
				Timeout.InfiniteTimeSpan);

			buffer.Append(
				LiveDebugEventKind.SessionNotice,
				$"The operator hold is released; the target auto-continues in {_autoContinueSeconds}s.");

			return CurrentStop();
		}
	}

	/// <summary>
	/// Evaluates a field-access expression against the stopped frame (issue #7): a root argument or local
	/// name, then <c>.field</c> steps into the object graph, read directly from memory. It runs none of
	/// the debuggee's own code -- no property getters, no method calls -- so it cannot hang or corrupt the
	/// target; those need func-eval, a deliberate non-goal for now. Returns an error, not a throw, when
	/// nothing is stopped or the expression does not resolve.
	/// </summary>
	public LiveEvaluation Evaluate(string expression)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint || _stoppedThread is null || _process is null || _detached || _exited)
			{
				return new LiveEvaluation { Expression = expression, Error = "The target is not stopped; evaluation needs a stop at a breakpoint or step." };
			}

			var parts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 0)
			{
				return new LiveEvaluation { Expression = expression, Error = "Empty expression." };
			}

			try
			{
				var current = ResolveRoot(_stoppedThread, parts[0]);
				if (current is null)
				{
					return new LiveEvaluation { Expression = expression, Error = $"'{parts[0]}' is not an argument or local in the current frame." };
				}

				for (var i = 1; i < parts.Length; i++)
				{
					current = ResolveField(current, parts[i]);
					if (current is null)
					{
						return new LiveEvaluation { Expression = expression, Error = $"Could not read '{parts[i]}' (not a field of the preceding value, or it is null)." };
					}
				}

				var (typeName, value) = ValueReader.Read(current);
				return new LiveEvaluation { Expression = expression, TypeName = typeName, Value = value };
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Evaluating '{Expression}' failed.", expression);
				return new LiveEvaluation { Expression = expression, Error = exception.Message };
			}
		}
	}

	/// <summary>Resolves a bare name to an argument or local value in the stopped top frame.</summary>
	private CorDebugValue? ResolveRoot(CorDebugThread thread, string name)
	{
		var frame = FindTopILFrame(thread);
		if (frame is null) return null;

		var function = frame.Function;
		var moduleName = TryModuleName(function);
		var methodToken = TryFunctionToken(function);
		var (isStatic, parameterNames) = methodToken is { } token && moduleName is not null
			? MethodTokens.ParameterNames(moduleName, token)
			: (true, (IReadOnlyList<string>)[]);

		var arguments = frame.EnumerateArguments().ToList();
		for (var i = 0; i < arguments.Count; i++)
		{
			if (ArgumentName(i, isStatic, parameterNames) == name) return arguments[i];
		}

		// Matched on the same name the variables were reported under, so a caller can pass back what
		// they were shown. A slot number still resolves where there are no symbols to give a name.
		var named = LocalNamesOf(frame);
		var locals = frame.EnumerateLocalVariables().ToList();
		for (var i = 0; i < locals.Count; i++)
		{
			if (LocalName(i, named) == name) return locals[i];
		}

		return null;
	}

	/// <summary>Reads a field off a value by name: dereference a reference, then read the field directly.</summary>
	private static CorDebugValue? ResolveField(CorDebugValue value, string fieldName)
	{
		if (value is CorDebugReferenceValue reference)
		{
			if (reference.IsNull) return null;
			value = reference.Dereference();
		}

		if (value is not CorDebugObjectValue objectValue) return null;

		var cls = objectValue.Class;
		var token = MethodTokens.FieldToken(cls.Module.Name, (int)cls.Token, fieldName);
		if (token is null) return null;

		// GetFieldValue wants the raw ICorDebugClass; the ClrDebug wrapper is not it (casting the
		// wrapper to the interface throws), so hand over its Raw.
		return objectValue.GetFieldValue(cls.Raw, token.Value);
	}

	/// <summary>
	/// Detaches, and terminates the debugging interface only if that worked.
	/// <para>
	/// <c>Terminate()</c>'s documented precondition is that every process has been detached from or
	/// terminated; running it while still attached is what takes the debuggee down with it. That
	/// precondition used to be written here as a comment asserting the fact rather than as a check
	/// of it, while <c>_detached</c> -- the field recording exactly that fact -- went unread.
	/// </para>
	/// <para>
	/// Leaking the interface for the few seconds until this host process exits is strictly better
	/// than killing the application somebody is running.
	/// </para>
	/// </summary>
	public void Dispose()
	{
		if (!Detach())
		{
			logger.LogWarning(
				"Leaving the ICorDebug interface open for pid {Pid}: the detach failed, and terminating it "
					+ "while still attached would take the target down.",
				TargetProcessId);

			return;
		}

		try
		{
			_corDebug?.Terminate();
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Terminating the ICorDebug interface failed.");
		}
	}

	private BreakpointBinding AddBinding(string location, bool stopOnHit, string? logMessage, int? logEveryNthHit, int? autoContinueSeconds, string? condition)
	{
		var parsed = SymbolLocation.Parse(location);
		var parsedCondition = BreakpointCondition.Parse(condition);

		BreakpointBinding binding;
		lock (_gate)
		{
			binding = new BreakpointBinding
			{
				Id = $"{(stopOnHit ? "bp" : "tp")}-{_nextBindingId++}",
				Location = parsed,
				Raw = location,
				StopOnHit = stopOnHit,
				LogMessage = logMessage,
				LogEveryNthHit = logEveryNthHit,
				AutoContinueSeconds = autoContinueSeconds,
				ConditionText = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim(),
				Condition = parsedCondition,
				Detail = "not bound yet",
			};
			_bindings.Add(binding);
		}

		BindAgainstLoadedModules();
		return binding;
	}

	private bool RemoveBinding(string id)
	{
		lock (_gate)
		{
			var binding = _bindings.FirstOrDefault(entry => entry.Id == id);
			if (binding is null) return false;

			try
			{
				binding.Breakpoint?.Activate(false);
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Deactivating binding {Id} failed.", id);
			}

			_bindings.Remove(binding);
			return true;
		}
	}

	/// <summary>
	/// Resumes a held target. <paramref name="generation"/> is the stop a timer was armed for, and is
	/// null when a caller asked.
	/// <para>
	/// Two things separate a timer's resume from a caller's. A timer whose generation has moved belongs
	/// to a stop that is already over, so it does nothing -- disposing a timer does not wait for a
	/// callback already running, so this is what makes a hold safe rather than nearly safe. And the
	/// safety timer must not fire while a person is holding the stop, which is the whole point of a
	/// hold; the hold's own expiry is exempt from that, since it is the thing the hold ends with.
	/// </para>
	/// </summary>
	private LiveContinueResult ContinueInternal(ResumeCause cause, long? generation)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint || _process is null || _detached || _exited)
			{
				return new LiveContinueResult { Continued = false };
			}

			var supersededTimer = generation is { } armedFor && armedFor != _stopGeneration;
			var blockedByHold = cause == ResumeCause.SafetyTimer && _holdUntilUtc is not null;

			// Both are no-ops rather than refusals: nothing asked for them.
			if (supersededTimer || blockedByHold) return new LiveContinueResult { Continued = false };

			var releasedHold = cause == ResumeCause.Caller && _holdUntilUtc is not null;

			_stoppedAtBreakpoint = false;
			var id = _stoppedBindingId;
			_stoppedBindingId = null;
			_stoppedThread = null;
			ClearStopTimers();

			try
			{
				_process.Continue(fIsOutOfBand: false);
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

	private DbgShim LoadDbgShim()
	{
		if (_shim is not null) return _shim;

		_shim = new DbgShim(NativeLibrary.Load(ResolveDbgShimPath()));
		return _shim;
	}

	/// <summary>
	/// dbgshim.dll must match this host's own architecture, since it loads the mscordbi that talks to
	/// the target. A RID-specific publish flattens it beside the exe; a plain build leaves it under
	/// <c>runtimes/&lt;rid&gt;/native</c> for the running RID. Handle both, matching the host's RID.
	/// </summary>
	private static string ResolveDbgShimPath()
	{
		var baseDir = AppContext.BaseDirectory;

		var flattened = Path.Combine(baseDir, "dbgshim.dll");
		if (File.Exists(flattened)) return flattened;

		var forThisRid = Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "dbgshim.dll");
		if (File.Exists(forThisRid)) return forThisRid;

		var runtimesRoot = Path.Combine(baseDir, "runtimes");
		if (Directory.Exists(runtimesRoot))
		{
			var any = Directory.EnumerateFiles(runtimesRoot, "dbgshim.dll", SearchOption.AllDirectories).FirstOrDefault();
			if (any is not null) return any;
		}

		throw new FileNotFoundException(
			"dbgshim.dll was not found beside the host or under runtimes/<rid>/native. The "
				+ "Microsoft.Diagnostics.DbgShim package should provide it for this architecture.",
			flattened);
	}

	/// <summary>
	/// A freshly started process has a pid before its CoreCLR loads, so the first EnumerateCLRs can
	/// find none. Retry briefly, then give up with a message that names the likely cause.
	/// </summary>
	private RuntimeInProcess FindRuntimeWithRetry(DbgShim shim, int pid, TimeSpan runtimeReadyTimeout)
	{
		var deadline = DateTime.UtcNow + runtimeReadyTimeout;
		while (true)
		{
			try
			{
				return FindRuntime(shim, pid);
			}
			catch (RuntimeNotReadyException)
			{
				if (DateTime.UtcNow >= deadline)
				{
					throw new InvalidOperationException(
						$"pid {pid} has no .NET (Core) runtime loaded. It may not be a .NET process, may be a "
							+ "different bitness than this host, or may be a .NET-native/AOT build with no ICorDebug.");
				}

				Thread.Sleep(100);
			}
		}
	}

	private static RuntimeInProcess FindRuntime(DbgShim shim, int pid)
	{
		var enumeration = shim.EnumerateCLRs(pid);
		if (enumeration.Items.Length == 0)
		{
			shim.CloseCLREnumeration(enumeration);
			throw new RuntimeNotReadyException(pid);
		}

		if (enumeration.Items.Length != 1)
		{
			shim.CloseCLREnumeration(enumeration);
			throw new InvalidOperationException(
				$"Expected one CLR in pid {pid}, found {enumeration.Items.Length}.");
		}

		var item = enumeration.Items[0];
		return new RuntimeInProcess(item.Path, item.Handle, enumeration);
	}

	private CorDebug CreateCorDebug(DbgShim shim, int pid, string runtimePath)
	{
		// The version string names the debuggee's coreclr; mscordbi is then loaded from beside it,
		// which is what makes this work for whatever runtime the target happens to be on.
		var version = shim.CreateVersionStringFromModule(pid, runtimePath);
		var (_, _, hmod) = RuntimeDiscovery.ParseVersionString(version);

		CorDebug created;
		try
		{
			created = shim.CreateDebuggingInterfaceFromVersionEx(CorDebugInterfaceVersion.CorDebugVersion_4_0, version);
		}
		catch (DebugException)
		{
			// dbgshim folds every failure on that path into one code; do its two steps by hand and
			// log what each saw, then create the object directly from the mscordbi beside the runtime.
			foreach (var line in RuntimeDiscovery.Probe(pid, hmod))
			{
				logger.LogDebug("ICorDebug create probe: {Line}", line);
			}

			created = RuntimeDiscovery.CreateCorDebug(runtimePath, pid, hmod, CorDebugInterfaceVersion.CorDebugVersion_4_0);
		}

		created.Initialize();

		var callback = new CorDebugManagedCallback();
		callback.OnAnyEvent += OnEvent;
		created.SetManagedHandler(callback);
		return created;
	}

	private void OnEvent(object? sender, CorDebugManagedCallbackEventArgs e)
	{
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
			_exited = true;
			return;
		}

		// A stopping breakpoint holds the target; Continue resumes it later.
		if (!shouldContinue) return;

		lock (_gate)
		{
			if (_detached) return;

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
					threadId: TryThreadId(log.Thread));
				return true;

			case BreakpointCorDebugManagedCallbackEventArgs hit:
				return RecordBreakpointHit(hit);

			case StepCompleteCorDebugManagedCallbackEventArgs step:
				return Hold(step.Thread, LiveDebugEventKind.StepComplete, "Step complete", bindingId: null, autoContinueSeconds: null);

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
		var typeName = DescribeExceptionType(exception.Thread);

		// The thread is stopped in this callback, so this is the moment its stack can be walked.
		var frames = WalkStack(exception.Thread, MaxStackFrames);

		buffer.Append(
			kind,
			$"{(unhandled ? "Unhandled" : "First-chance")} {typeName} on thread {TryThreadId(exception.Thread)?.ToString() ?? "?"}",
			threadId: TryThreadId(exception.Thread),
			exceptionType: typeName,
			frames: frames.Count > 0 ? frames : null);
	}

	private bool RecordBreakpointHit(BreakpointCorDebugManagedCallbackEventArgs hit)
	{
		var threadId = TryThreadId(hit.Thread);
		var (token, moduleName) = TryFunctionIdentity(hit.Breakpoint as CorDebugFunctionBreakpoint);

		BreakpointBinding? binding;
		long ordinal;
		lock (_gate)
		{
			binding = _bindings.FirstOrDefault(entry =>
				entry.Bound
				&& entry.Token == token
				&& (moduleName is null
					|| string.Equals(Path.GetFileNameWithoutExtension(moduleName), entry.Location.ModuleSimpleName, StringComparison.OrdinalIgnoreCase)));

			// With one binding bound, an unidentified hit is unambiguously it.
			binding ??= _bindings.Count(entry => entry.Bound) == 1 ? _bindings.First(entry => entry.Bound) : null;
			ordinal = binding is null ? 0 : ++binding.HitCount;
		}

		// A condition is a cheap read-and-compare on the stopped frame; if it fails, act as if unhit.
		if (binding?.Condition is { } condition && !condition.Evaluate(ReadTopFrameVariables(hit.Thread)))
		{
			return true;
		}

		if (binding is { StopOnHit: true })
		{
			return Hold(hit.Thread, LiveDebugEventKind.BreakpointHit, $"Breakpoint {binding.Raw} hit #{ordinal}", binding.Id, binding.AutoContinueSeconds);
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
	private bool Hold(CorDebugThread thread, LiveDebugEventKind kind, string prefix, string? bindingId, int? autoContinueSeconds)
	{
		var frames = WalkStack(thread, MaxStackFrames);
		var variables = ReadTopFrameVariables(thread);
		var threadId = TryThreadId(thread);
		var top = frames.Count > 0 ? frames[0] : "?";

		lock (_gate)
		{
			_stoppedAtBreakpoint = true;
			_stoppedBindingId = bindingId;
			_stoppedThread = thread;

			var seconds = autoContinueSeconds ?? DefaultAutoContinueSeconds;

			// Before the timer is armed, so a callback that fires immediately cannot see a half-built
			// stop, and so the generation the timer captures is this stop's.
			var generation = ++_stopGeneration;
			_autoContinueSeconds = seconds;
			_stoppedAtUtc = DateTime.UtcNow;
			_autoContinueAtUtc = _stoppedAtUtc.AddSeconds(seconds);

			_stopEventSequence = buffer.Append(
				kind,
				$"{prefix} at {top} on thread {threadId?.ToString() ?? "?"} -- stopped; continue or step (auto-continues in {seconds}s).",
				threadId: threadId,
				frames: frames.Count > 0 ? frames : null,
				variables: variables.Count > 0 ? variables : null);

			ClearStopTimers();
			_autoContinueTimer = new Timer(
				_ => ContinueInternal(ResumeCause.SafetyTimer, generation),
				null,
				TimeSpan.FromSeconds(seconds),
				Timeout.InfiniteTimeSpan);
		}

		return false;
	}

	/// <summary>
	/// The top managed frame's arguments and locals, read while the thread is stopped. Argument names come
	/// from metadata (an instance method's argument 0 is <c>this</c>); local names come from the module's
	/// portable PDB, resolved for the scopes covering the frame's own IL offset, and fall back to
	/// <c>local_0</c> upwards by slot where there are no symbols to name them. Reading is defensive per
	/// variable, so one unreadable value does not lose the rest of the frame.
	/// </summary>
	private IReadOnlyList<LiveVariable> ReadTopFrameVariables(CorDebugThread thread)
	{
		var variables = new List<LiveVariable>();
		try
		{
			var frame = FindTopILFrame(thread);
			if (frame is null) return variables;

			var function = frame.Function;
			var moduleName = TryModuleName(function);
			var methodToken = TryFunctionToken(function);

			var (isStatic, parameterNames) = methodToken is { } token && moduleName is not null
				? MethodTokens.ParameterNames(moduleName, token)
				: (true, (IReadOnlyList<string>)[]);

			var arguments = frame.EnumerateArguments().ToList();
			for (var i = 0; i < arguments.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value) = ValueReader.Read(arguments[i]);
				variables.Add(new LiveVariable { Name = ArgumentName(i, isStatic, parameterNames), Kind = "argument", TypeName = typeName, Value = value });
			}

			var named = LocalNamesOf(frame);
			var locals = frame.EnumerateLocalVariables().ToList();
			for (var i = 0; i < locals.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value) = ValueReader.Read(locals[i]);
				variables.Add(new LiveVariable { Name = LocalName(i, named), Kind = "local", TypeName = typeName, Value = value });
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading the stopped frame's variables failed.");
		}

		return variables;
	}

	private static CorDebugILFrame? FindTopILFrame(CorDebugThread thread)
	{
		foreach (var chain in thread.EnumerateChains())
		{
			foreach (var frame in chain.EnumerateFrames())
			{
				if (frame is CorDebugILFrame ilFrame) return ilFrame;
			}
		}

		return null;
	}

	/// <summary>
	/// What a frame's locals are called, by slot, read from its module's portable PDB at the IL
	/// offset the frame is stopped at. Empty when there are no symbols for the module.
	/// <para>
	/// The offset is not decoration. A slot is reused by locals in sibling blocks, so the names in
	/// scope depend on where execution is -- asking for every name in the method would hand back two
	/// names for one slot and pick between them arbitrarily.
	/// </para>
	/// </summary>
	private static IReadOnlyDictionary<int, string> LocalNamesOf(CorDebugILFrame frame)
	{
		var none = new Dictionary<int, string>();

		try
		{
			var function = frame.Function;
			var moduleName = TryModuleName(function);
			var methodToken = TryFunctionToken(function);

			if (moduleName is null || methodToken is not { } token) return none;
			if (SymbolCache.Shared.For(moduleName)?.Pdb is not { } pdb) return none;

			return pdb.LocalNames(token, frame.IP.pnOffset);
		}
		catch (Exception)
		{
			return none;
		}
	}

	/// <summary>
	/// A local's name: what the PDB calls it, or its slot when there are no symbols.
	/// <para>
	/// The fallback is a real answer rather than a placeholder. A release build, a framework
	/// assembly, or a PDB belonging to another build all leave a debugger with nothing but slots, and
	/// a slot number is at least true -- which is why a mismatched PDB is refused rather than read.
	/// </para>
	/// </summary>
	private static string LocalName(int slot, IReadOnlyDictionary<int, string> named) =>
		named.TryGetValue(slot, out var name) ? name : $"local_{slot}";

	private static string ArgumentName(int index, bool isStatic, IReadOnlyList<string> parameterNames)
	{
		if (!isStatic)
		{
			if (index == 0) return "this";
			var parameter = index - 1;
			return parameter < parameterNames.Count ? parameterNames[parameter] : $"arg_{index}";
		}

		return index < parameterNames.Count ? parameterNames[index] : $"arg_{index}";
	}

	private static string? TryModuleName(CorDebugFunction function)
	{
		try
		{
			return function.Module.Name;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static int? TryFunctionToken(CorDebugFunction function)
	{
		try
		{
			return (int)function.Token;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// The managed frames of a stopped thread, innermost first, resolved to method names. Only valid
	/// while the thread is stopped -- which, for an exception or a stopping breakpoint, is the callback
	/// it is reported on. Frames whose function cannot be resolved (native, internal, dynamic) are
	/// skipped.
	/// </summary>
	private IReadOnlyList<string> WalkStack(CorDebugThread thread, int maxFrames)
	{
		var frames = new List<string>();
		try
		{
			foreach (var chain in thread.EnumerateChains())
			{
				foreach (var frame in chain.EnumerateFrames())
				{
					if (frames.Count >= maxFrames) return frames;

					var described = DescribeFrame(frame);
					if (described is not null) frames.Add(described);
				}
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Walking a thread's stack failed.");
		}

		return frames;
	}

	private static string? DescribeFrame(CorDebugFrame frame)
	{
		try
		{
			var function = frame.Function;
			return MethodTokens.MethodFullName(function.Module.Name, (int)function.Token);
		}
		catch (Exception)
		{
			return null; // Native, internal, or otherwise unresolvable frame.
		}
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
			if (_process is null || _detached || _exited) return;
			if (_bindings.TrueForAll(binding => binding.Bound)) return;

			var stopped = false;
			try
			{
				_process.Stop(0);
				stopped = true;

				var loaded = new List<string>();
				foreach (var module in EnumerateModules(_process))
				{
					loaded.Add(SimpleName(module));
					foreach (var binding in _bindings)
					{
						if (!binding.Bound) TryBind(binding, module);
					}
				}

				ExplainUnbound(loaded);
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
						_process.Continue(fIsOutOfBand: false);
					}
					catch (Exception exception)
					{
						logger.LogDebug(exception, "Continue after bind failed.");
					}
				}
			}
		}
	}

	/// <summary>Binds unbound bindings against a module as it loads; called from a stopped callback.</summary>
	private void BindModule(CorDebugModule module)
	{
		lock (_gate)
		{
			if (_bindings.TrueForAll(binding => binding.Bound)) return;

			foreach (var binding in _bindings)
			{
				if (!binding.Bound) TryBind(binding, module);
			}
		}
	}

	private void TryBind(BreakpointBinding binding, CorDebugModule module)
	{
		if (binding.Bound) return;

		try
		{
			if (module.IsDynamic || module.IsInMemory) return;
		}
		catch (Exception)
		{
			return; // A module that cannot describe itself is not one we can read metadata from.
		}

		if (!string.Equals(SimpleName(module), binding.Location.ModuleSimpleName, StringComparison.OrdinalIgnoreCase)) return;

		var token = MethodTokens.Find(module.Name, binding.Location.TypeName, binding.Location.MethodName);
		if (token is null)
		{
			binding.Detail = $"no method {binding.Location.TypeName}.{binding.Location.MethodName} in {Path.GetFileName(module.Name)}";
			return;
		}

		try
		{
			var function = module.GetFunctionFromToken(token.Value);
			var breakpoint = function.CreateBreakpoint();
			breakpoint.Activate(true);

			binding.Breakpoint = breakpoint;
			binding.Token = token.Value;
			binding.Detail = null;
			buffer.Append(LiveDebugEventKind.SessionNotice, $"{binding.Id} bound at {binding.Raw}.");
			logger.LogInformation("Binding {Id} bound at {Location} (token 0x{Token:x8}).", binding.Id, binding.Raw, token.Value);
		}
		catch (Exception exception)
		{
			binding.Detail = $"bind failed: {exception.Message}";
			logger.LogDebug(exception, "Binding {Id} at {Location} failed.", binding.Id, binding.Raw);
		}
	}

	/// <summary>
	/// Says why each still-unbound binding is unbound, against the modules that are actually loaded.
	/// <para>
	/// This exists because the answer used to be a guess dressed as a fact. A binding started life
	/// saying "module not loaded yet" and kept saying it however the bind had failed, so a bare
	/// <c>Namespace.Type.Method</c> whose module name was inferred wrongly -- the module is guessed
	/// from the first namespace segment, which is only right when the assembly is named for its root
	/// namespace -- reported that the module had not loaded while the event stream carried its load at
	/// sequence 7. Two different failures, one message, and the one it chose pointed the caller at
	/// waiting rather than at the spelling.
	/// </para>
	/// <para>
	/// Only ever narrows: a detail already set by <see cref="TryBind"/> is a real finding about a
	/// module that matched, and is left alone.
	/// </para>
	/// </summary>
	private void ExplainUnbound(IReadOnlyList<string> loadedModules)
	{
		foreach (var binding in _bindings)
		{
			if (binding.Bound) continue;
			if (binding.Detail is not (null or "not bound yet")) continue;

			var wanted = binding.Location.ModuleSimpleName;
			if (loadedModules.Contains(wanted, StringComparer.OrdinalIgnoreCase))
			{
				// The module is loaded and TryBind said nothing, so the type is what is missing --
				// the method-level miss is reported by TryBind itself.
				binding.Detail = $"no type {binding.Location.TypeName} in {wanted}";
				continue;
			}

			if (!binding.Location.ModuleWasInferred)
			{
				binding.Detail = $"module {wanted} is not loaded ({loadedModules.Count} others are)";
				continue;
			}

			// The actionable case, and the one that was being reported as a wait.
			binding.Detail = $"no loaded module is named {wanted}, which was inferred from the type name; "
				+ "give the assembly explicitly as Assembly!Namespace.Type.Method";
		}
	}

	private static string SimpleName(CorDebugModule module) => Path.GetFileNameWithoutExtension(module.Name);

	private static IEnumerable<CorDebugModule> EnumerateModules(CorDebugProcess process)
	{
		foreach (var appDomain in process.AppDomains)
		{
			foreach (var assembly in appDomain.Assemblies)
			{
				foreach (var module in assembly.Modules)
				{
					yield return module;
				}
			}
		}
	}

	private (int? Token, string? Module) TryFunctionIdentity(CorDebugFunctionBreakpoint? breakpoint)
	{
		if (breakpoint is null) return (null, null);

		try
		{
			var function = breakpoint.Function;
			return ((int)function.Token, function.Module.Name);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading a breakpoint's function identity failed.");
			return (null, null);
		}
	}

	/// <summary>
	/// Stands down everything that would resume the current stop: the safety timer, an operator's hold
	/// and its timer. Called wherever a stop ends or begins, so a new stop never inherits the previous
	/// one's hold.
	/// <para>
	/// Disposing a timer does not wait for a callback already running, which is why the stop generation
	/// exists rather than this being enough on its own.
	/// </para>
	/// </summary>
	private void ClearStopTimers()
	{
		_autoContinueTimer?.Dispose();
		_autoContinueTimer = null;
		_holdTimer?.Dispose();
		_holdTimer = null;
		_holdUntilUtc = null;
	}

	/// <summary>Wraps a native event handle so it can be waited on or set through the BCL.</summary>
	private static EventWaitHandle WrapEvent(IntPtr handle, bool ownsHandle)
	{
		var wrapped = new EventWaitHandle(false, EventResetMode.AutoReset);
		wrapped.SafeWaitHandle.Dispose();
		wrapped.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle);
		return wrapped;
	}

	private static LiveTracepoint DescribeTracepoint(BreakpointBinding binding) => new()
	{
		Id = binding.Id,
		Location = binding.Raw,
		Bound = binding.Bound,
		HitCount = binding.HitCount,
		LogMessage = binding.LogMessage,
		LogEveryNthHit = binding.LogEveryNthHit,
		Condition = binding.ConditionText,
		Detail = binding.Bound ? null : binding.Detail,
	};

	private static LiveBreakpoint DescribeBreakpoint(BreakpointBinding binding) => new()
	{
		Id = binding.Id,
		Location = binding.Raw,
		StopOnHit = binding.StopOnHit,
		Bound = binding.Bound,
		HitCount = binding.HitCount,
		AutoContinueSeconds = binding.AutoContinueSeconds ?? DefaultAutoContinueSeconds,
		Condition = binding.ConditionText,
		Detail = binding.Bound ? null : binding.Detail,
	};

	private static string DescribeExceptionType(CorDebugThread thread)
	{
		try
		{
			var value = thread.CurrentException;
			if (value is CorDebugReferenceValue reference)
			{
				value = reference.Dereference();
			}

			if (value is CorDebugObjectValue obj)
			{
				var cls = obj.Class;
				return MethodTokens.TypeName(cls.Module.Name, cls.Token) ?? $"type token 0x{(int)cls.Token:x8}";
			}

			return value?.GetType().Name ?? "(no exception object)";
		}
		catch (Exception)
		{
			return "(unresolved exception type)";
		}
	}

	private static int? TryThreadId(CorDebugThread thread)
	{
		try
		{
			return thread.Id;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private sealed record RuntimeInProcess(string Path, IntPtr Handle, EnumerateCLRsResult Enumeration);

	private sealed class BreakpointBinding
	{
		public required string Id { get; init; }

		public required SymbolLocation Location { get; init; }

		/// <summary>The location as the caller wrote it, for reporting.</summary>
		public required string Raw { get; init; }

		/// <summary>True for a stopping breakpoint; false for a tracepoint (log and continue).</summary>
		public required bool StopOnHit { get; init; }

		public string? LogMessage { get; init; }

		public int? LogEveryNthHit { get; init; }

		public int? AutoContinueSeconds { get; init; }

		/// <summary>The condition as the caller wrote it, for reporting; null when there is none.</summary>
		public string? ConditionText { get; init; }

		/// <summary>The parsed condition evaluated on each hit; null when there is none.</summary>
		public BreakpointCondition? Condition { get; init; }

		public long HitCount { get; set; }

		/// <summary>The bound method's metadata token, used to match a hit back to this binding.</summary>
		public int? Token { get; set; }

		public CorDebugFunctionBreakpoint? Breakpoint { get; set; }

		public string? Detail { get; set; }

		public bool Bound => Breakpoint is not null;
	}
}

/// <summary>The process exists but its CoreCLR has not loaded yet; the caller may retry.</summary>
internal sealed class RuntimeNotReadyException(int pid)
	: Exception($"pid {pid} has no CoreCLR loaded yet.");
