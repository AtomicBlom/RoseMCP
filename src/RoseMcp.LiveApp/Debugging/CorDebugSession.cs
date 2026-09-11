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
	/// The deepest a structured stack walk goes. A stack is bounded because a runaway recursion has
	/// tens of thousands of frames and reading each one costs metadata lookups, on a path a person
	/// is waiting on; the answer says it was cut short rather than implying the stack ended.
	/// </summary>
	private const int MaxStructuredFrames = 200;

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

	/// <summary>
	/// How many times to try detaching before giving up. Failure under contention is plausibly
	/// transient and a retry is far cheaper than what failing costs the person whose app it is.
	/// </summary>
	private const int DetachAttempts = 3;

	private static readonly TimeSpan DetachRetryDelay = TimeSpan.FromMilliseconds(50);

	private readonly Lock _gate = new();
	private readonly List<BreakpointBinding> _bindings = [];
	private int _nextBindingId = 1;

	/// <summary>
	/// Every module file the target has loaded, so its metadata and symbols can be read off disk.
	/// <para>
	/// Kept rather than asked for each time, because asking means synchronizing the target and a
	/// search runs on every keystroke of an autocomplete. What is inside a module is on disk, so once
	/// the path is known nothing else about the answer needs the debuggee at all.
	/// </para>
	/// </summary>
	private readonly List<string> _modulePaths = [];

	/// <summary>
	/// Whether the modules already loaded when this session attached have been enumerated. The load
	/// callback covers everything after the attach; a process that was already running needs the one
	/// walk, and it is taken lazily so a session nobody searches never pays for it.
	/// </summary>
	private bool _modulesEnumerated;

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
		if (limit < 1) throw new ArgumentException("limit must be at least 1.", nameof(limit));

		var typed = query?.Trim() ?? string.Empty;
		var paths = ModulePaths();
		var found = MethodSearch.Search(paths, typed, limit);

		return new LiveMethodMatches
		{
			Query = typed,
			Matches = [.. found.Matches.Select(DescribeMatch)],
			Total = found.Total,
			ModulesSearched = found.ModulesSearched,
			Detail = SearchDetail(typed, paths.Count, found),
		};
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
	public LiveMethodSource ReadMethodSource(string location)
	{
		var parsed = SymbolLocation.Parse(location);
		var displayName = MethodDisplayName.Of(parsed.TypeName, parsed.MethodName);
		var module = parsed.ModuleSimpleName;

		var modulePath = ModulePaths().FirstOrDefault(path =>
			string.Equals(Path.GetFileNameWithoutExtension(path), module, StringComparison.OrdinalIgnoreCase));

		if (modulePath is null)
		{
			return NoMethodSource(
				location, displayName, module, LiveSymbolState.NoSymbols, $"No loaded module is named {module}.");
		}

		var symbols = SymbolCache.Shared.For(modulePath);
		var token = MethodTokens.Find(modulePath, parsed.TypeName, parsed.MethodName);
		if (symbols is null || token is null)
		{
			return NoMethodSource(
				location,
				displayName,
				module,
				LiveSymbolState.NoSymbols,
				$"{Path.GetFileName(modulePath)} declares no method {parsed.TypeName}.{parsed.MethodName}.");
		}

		if (symbols.Pdb is null)
		{
			var state = symbols.PdbState == PdbState.Mismatched
				? LiveSymbolState.SymbolsMismatched
				: LiveSymbolState.NoSymbols;

			return NoMethodSource(
				location,
				displayName,
				module,
				state,
				(symbols.PdbProblem ?? $"{Path.GetFileName(modulePath)} has no symbols on this machine.")
					+ " A breakpoint on this method still stops at its first instruction.");
		}

		var region = MethodRegion.Of(symbols.Pdb.Extents(), token.Value);
		if (MethodRegion.Lines(region) is not { } span)
		{
			return NoMethodSource(
				location,
				displayName,
				module,
				LiveSymbolState.NoSequencePoint,
				"The symbols record no source for this method, which is what an abstract, external or "
					+ "generated one looks like. A breakpoint on it still stops at its first instruction.");
		}

		var excerpt = SourceLines.Read(span.File, span.FirstLine, span.LastLine);

		return new LiveMethodSource
		{
			Location = location,
			DisplayName = displayName,
			Module = module,
			Symbols = LiveSymbolState.Resolved,
			File = span.File,
			FirstLine = excerpt.FirstLine,
			Lines = excerpt.Lines,
			Positions = PositionsIn(region, modulePath, module, span.File),
			Detail = excerpt.HasText
				? excerpt.Problem
				: $"{excerpt.Problem} The positions below are still exact; only the text is missing.",
		};
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
	/// Takes or releases an operator's hold on the current stop, and reports what the stop now is.
	/// <para>
	/// One entry point for both directions because the answer is the same shape either way and the
	/// caller wants the stop back: a hold is only meaningful next to the deadline it moved.
	/// </para>
	/// </summary>
	/// <param name="requested">How long to hold for, or null for the default. Ignored on a release.</param>
	/// <param name="release">Give the stop back to the safety timer rather than holding it.</param>
	public LiveHoldResult Hold(TimeSpan? requested, bool release)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint)
			{
				return new LiveHoldResult
				{
					Execution = LiveExecutionState.Running,
					Applied = false,
					Detail = "Nothing is stopped, so there is no stop to hold. A hold suspends the safety timer on a "
						+ "target already held at a breakpoint or a step.",
				};
			}

			var wasHeld = _holdUntilUtc is not null;
			var stop = release ? ReleaseHold() : SetHold(requested);

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
	/// Suspends the safety timer while somebody reads this stop, and reports the stop as it now
	/// stands -- or null when there is nothing stopped to hold. Asking again extends it.
	/// <para>
	/// Bounded at <see cref="MaxHoldSeconds"/> however long is asked for, and the bound is the point
	/// rather than a formality: the safety timer exists so an unattended stop cannot wedge somebody's
	/// app, and a hold with no limit would hand that failure back under another name.
	/// </para>
	/// </summary>
	private LiveStop? SetHold(TimeSpan? requested)
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
	private LiveStop? ReleaseHold()
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

		var page = Math.Min(limit ?? DefaultFrameLimit, MaxStructuredFrames);

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

			var thread = FindThread(threadId);
			var walked = WalkIlFrames(thread);
			var active = threadId is null || threadId == TryThreadId(thread);
			var id = TryThreadId(thread) ?? 0;

			var frames = new List<LiveStackFrame>();
			for (var index = offset; index < walked.Frames.Count && frames.Count < page; index++)
			{
				frames.Add(DescribeStackFrame(walked.Frames[index], index, id, active && index == 0));
			}

			return new LiveStackFrames
			{
				Execution = stop.State,
				Stop = stop,
				ThreadId = id,
				Frames = frames,
				Offset = offset,
				Total = walked.Frames.Count,
				Truncated = walked.Truncated,
			};
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

			var thread = FindThread(threadId);
			var frame = FrameAt(thread, frameIndex);
			var (module, token) = FrameIdentity(frame);
			var (variables, truncated) = ReadVariables(frame);

			return new LiveFrameVariables
			{
				Execution = stop.State,
				Stop = stop,
				FrameIndex = frameIndex,
				ThreadId = TryThreadId(thread),
				MethodFullName = module is null || token is null ? null : MethodTokens.MethodFullName(module, token.Value),
				Symbols = SymbolsOf(module, token, IlOffsetOf(frame)).State,
				Variables = variables,
				Truncated = truncated,
			};
		}
	}

	/// <summary>
	/// What is inside a value: an object's fields, or an array's elements, addressed by the same
	/// <see cref="LiveVariable.Path"/> the value was reported under.
	/// <para>
	/// No debuggee code runs, so a property with a getter is not among the children -- what comes
	/// back is what the object holds. Statics are out of scope: they belong to a type rather than to
	/// the value in hand, and a caller asking about a value has not asked about its type.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The path does not parse, the frame is not there, or the path resolves to nothing.</exception>
	public LiveValueExpansion Expand(string path, int frameIndex, int? threadId)
	{
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

			var frame = FrameAt(FindThread(threadId), frameIndex);
			var value = Resolve(frame, parsed, out var failure)
				?? throw new ArgumentException(failure ?? $"'{path}' does not resolve in frame {frameIndex}.");

			var (typeName, rendered, hasChildren) = ValueReader.Read(value);
			var (children, total, truncated) = hasChildren ? ChildrenOf(value, path) : ([], 0, false);

			return new LiveValueExpansion
			{
				Execution = stop.State,
				Stop = stop,
				Path = path,
				TypeName = typeName,
				Value = rendered,
				Children = children,
				Total = total,
				Truncated = truncated,
			};
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

			var held = _stoppedThread is null ? null : TryThreadId(_stoppedThread);
			var threads = new List<LiveThread>();

			try
			{
				foreach (var thread in _process!.EnumerateThreads())
				{
					var id = TryThreadId(thread);
					if (id is null) continue;

					threads.Add(new LiveThread
					{
						Id = id.Value,
						UserState = UserStateOf(thread),
						IsStopped = id == held,
						TopFrame = TopFrameName(thread),
					});
				}
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Enumerating the target's threads failed.");
			}

			return new LiveThreadList
			{
				Execution = stop.State,
				Stop = stop,

				// The held thread first: it is the one every other answer is about by default, and a
				// reader scanning forty pool threads for it should not have to.
				Threads = [.. threads.OrderByDescending(thread => thread.IsStopped).ThenBy(thread => thread.Id)],
			};
		}
	}

	/// <summary>The thread a caller named, or the one being held when it named none.</summary>
	/// <exception cref="ArgumentException">No thread of that id, or nothing is held and none was named.</exception>
	private CorDebugThread FindThread(int? threadId)
	{
		if (threadId is not { } wanted)
		{
			return _stoppedThread ?? throw new ArgumentException("No thread is being held, so there is no default thread to read.");
		}

		if (_process is null) throw new ArgumentException("The session has no process to read threads from.");

		foreach (var thread in _process.EnumerateThreads())
		{
			if (TryThreadId(thread) == wanted) return thread;
		}

		throw new ArgumentException($"The target has no thread {wanted}. rose_live_app_threads lists the ones it has.");
	}

	/// <summary>
	/// A thread's managed IL frames, innermost first, with how many unrepresentable frames preceded
	/// each -- native, internal or dynamic frames the runtime gives no IL frame for.
	/// <para>
	/// Counting them rather than dropping them is what keeps the stack honest: a stack silently
	/// missing three frames reads as a complete stack with a surprising caller.
	/// </para>
	/// </summary>
	private FrameWalk WalkIlFrames(CorDebugThread thread)
	{
		var frames = new List<WalkedFrame>();
		var skipped = 0;

		try
		{
			foreach (var chain in thread.EnumerateChains())
			{
				foreach (var frame in chain.EnumerateFrames())
				{
					if (frames.Count >= MaxStructuredFrames) return new FrameWalk(frames, Truncated: true);

					if (frame is CorDebugILFrame ilFrame)
					{
						frames.Add(new WalkedFrame(ilFrame, skipped));
						skipped = 0;
						continue;
					}

					skipped++;
				}
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Walking a thread's frames failed.");
		}

		return new FrameWalk(frames, Truncated: false);
	}

	/// <exception cref="ArgumentException">There is no frame at that index on this thread.</exception>
	private CorDebugILFrame FrameAt(CorDebugThread thread, int index)
	{
		var walked = WalkIlFrames(thread);
		if (index < walked.Frames.Count) return walked.Frames[index].Frame;

		throw new ArgumentException(
			$"Frame {index} is past the end of this thread's stack, which has {walked.Frames.Count} managed frame(s).");
	}

	private LiveStackFrame DescribeStackFrame(WalkedFrame walked, int index, int threadId, bool isActive)
	{
		var frame = walked.Frame;
		var (module, token) = FrameIdentity(frame);
		var offset = IlOffsetOf(frame);
		var (state, source) = SymbolsOf(module, token, offset);

		return new LiveStackFrame
		{
			Index = index,
			ThreadId = threadId,
			Module = module is null ? null : Path.GetFileName(module),
			MethodFullName = module is null || token is null ? null : MethodTokens.MethodFullName(module, token.Value),
			IlOffset = offset,
			Mapping = MappingOf(frame),
			Symbols = state,
			Source = source,
			IsActive = isActive,
			SkippedBefore = walked.SkippedBefore,
		};
	}

	/// <summary>The module path and method token behind a frame, either null when it cannot be read.</summary>
	private static (string? Module, int? Token) FrameIdentity(CorDebugILFrame frame)
	{
		try
		{
			var function = frame.Function;
			return (TryModuleName(function), TryFunctionToken(function));
		}
		catch (Exception)
		{
			return (null, null);
		}
	}

	private static int? IlOffsetOf(CorDebugILFrame frame)
	{
		try
		{
			return frame.IP.pnOffset;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static LiveIlMapping MappingOf(CorDebugILFrame frame)
	{
		try
		{
			var mapping = frame.IP.pMappingResult;

			// Tested in order of how much a reader can rely on the line beside it, because the
			// runtime sets these as flags and more than one can be on at once.
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_EXACT)) return LiveIlMapping.Exact;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_APPROXIMATE)) return LiveIlMapping.Approximate;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_PROLOG)) return LiveIlMapping.Prolog;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_EPILOG)) return LiveIlMapping.Epilog;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS)) return LiveIlMapping.UnmappedAddress;

			return LiveIlMapping.NoInfo;
		}
		catch (Exception)
		{
			return LiveIlMapping.NoInfo;
		}
	}

	/// <summary>
	/// Whether a module's symbols could name this instruction's source, and the position when they
	/// could. A mismatched PDB is its own state rather than "no symbols", because it is the one a
	/// person can act on.
	/// </summary>
	private static (LiveSymbolState State, LiveSourcePosition? Source) SymbolsOf(string? module, int? token, int? ilOffset)
	{
		if (module is null || token is not { } methodToken) return (LiveSymbolState.NoSymbols, null);

		var symbols = SymbolCache.Shared.For(module);
		if (symbols is null) return (LiveSymbolState.NoSymbols, null);
		if (symbols.PdbState == PdbState.Mismatched) return (LiveSymbolState.SymbolsMismatched, null);
		if (symbols.Pdb is not { } pdb) return (LiveSymbolState.NoSymbols, null);

		if (ilOffset is not { } offset) return (LiveSymbolState.NoSequencePoint, null);
		if (pdb.Position(methodToken, offset) is not { } position) return (LiveSymbolState.NoSequencePoint, null);

		return (LiveSymbolState.Resolved, new LiveSourcePosition
		{
			File = position.File,
			Line = position.Line,
			Column = position.Column,
			EndLine = position.EndLine,
			EndColumn = position.EndColumn,
		});
	}

	/// <summary>The runtime's thread state as words, without the <c>USER_</c> every one of them carries.</summary>
	private static IReadOnlyList<string> UserStateOf(CorDebugThread thread)
	{
		try
		{
			var state = thread.UserState;

			return
			[
				.. Enum.GetValues<CorDebugUserState>()
					.Where(flag => flag != 0 && state.HasFlag(flag))
					.Select(flag => flag.ToString().Replace("USER_", string.Empty, StringComparison.Ordinal))
					.Select(Titled),
			];
		}
		catch (Exception)
		{
			return [];
		}
	}

	/// <summary><c>WAIT_SLEEP_JOIN</c> as <c>WaitSleepJoin</c>, which is how .NET spells it everywhere else.</summary>
	private static string Titled(string screaming) =>
		string.Concat(screaming.Split('_').Select(word => word.Length == 0
			? word
			: char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

	private string? TopFrameName(CorDebugThread thread)
	{
		var walked = WalkIlFrames(thread);
		if (walked.Frames.Count == 0) return null;

		var (module, token) = FrameIdentity(walked.Frames[0].Frame);

		return module is null || token is null ? null : MethodTokens.MethodFullName(module, token.Value);
	}

	/// <summary>
	/// A frame's arguments and locals, named and addressed. Reading is defensive per variable, so
	/// one unreadable value does not lose the rest of the frame.
	/// </summary>
	private (IReadOnlyList<LiveVariable> Variables, bool Truncated) ReadVariables(CorDebugILFrame frame)
	{
		var variables = new List<LiveVariable>();
		var total = 0;

		try
		{
			var (module, token) = FrameIdentity(frame);
			var (isStatic, parameterNames) = token is { } methodToken && module is not null
				? MethodTokens.ParameterNames(module, methodToken)
				: (true, (IReadOnlyList<string>)[]);

			var arguments = frame.EnumerateArguments().ToList();
			var named = LocalNamesOf(frame);
			var locals = frame.EnumerateLocalVariables().ToList();
			total = arguments.Count + locals.Count;

			for (var i = 0; i < arguments.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value, hasChildren) = ValueReader.Read(arguments[i]);
				variables.Add(new LiveVariable
				{
					Name = ArgumentName(i, isStatic, parameterNames),
					Kind = "argument",
					TypeName = typeName,
					Value = value,
					Path = ValuePath.Argument(i),
					HasChildren = hasChildren,
				});
			}

			for (var i = 0; i < locals.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value, hasChildren) = ValueReader.Read(locals[i]);
				variables.Add(new LiveVariable
				{
					Name = LocalName(i, named),
					Kind = "local",
					TypeName = typeName,
					Value = value,
					Path = ValuePath.Local(i),
					HasChildren = hasChildren,
				});
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading a frame's variables failed.");
		}

		return (variables, variables.Count < total);
	}

	/// <summary>
	/// Walks a parsed path from a frame to the value it names, or null with a sentence saying which
	/// step failed. The sentence names the step rather than the whole path, because a five-segment
	/// path that resolves four segments and fails on the fifth is a different problem from one that
	/// was wrong from the start.
	/// </summary>
	private CorDebugValue? Resolve(CorDebugILFrame frame, ValuePath path, out string? failure)
	{
		failure = null;
		var current = ResolveValueRoot(frame, path, out failure);
		if (current is null) return null;

		var walked = path.Kind switch
		{
			ValuePathRoot.Argument => ValuePath.Argument(path.Slot),
			ValuePathRoot.Local => ValuePath.Local(path.Slot),
			_ => path.Name ?? string.Empty,
		};

		foreach (var step in path.Steps)
		{
			if (step.Field is { } field)
			{
				current = ResolveField(current, field);
				if (current is null)
				{
					failure = $"'{walked}' has no readable field '{field}' (not a field of that value, or it is null).";
					return null;
				}

				walked = ValuePath.Field(walked, field);
				continue;
			}

			var index = step.Index ?? 0;
			current = ResolveElement(current, index, out failure);
			if (current is null) return null;

			walked = ValuePath.Element(walked, index);
		}

		return current;
	}

	/// <summary>Resolves a path's root: an argument slot, a local slot, or a name in the frame.</summary>
	private CorDebugValue? ResolveValueRoot(CorDebugILFrame frame, ValuePath path, out string? failure)
	{
		failure = null;

		try
		{
			if (path.Kind == ValuePathRoot.Argument)
			{
				var arguments = frame.EnumerateArguments().ToList();
				if (path.Slot < arguments.Count) return arguments[path.Slot];

				failure = $"This frame has {arguments.Count} argument(s), so there is no arg:{path.Slot}.";
				return null;
			}

			if (path.Kind == ValuePathRoot.Local)
			{
				var locals = frame.EnumerateLocalVariables().ToList();
				if (path.Slot < locals.Count) return locals[path.Slot];

				failure = $"This frame has {locals.Count} local(s), so there is no local:{path.Slot}.";
				return null;
			}

			var name = path.Name ?? string.Empty;
			var found = ResolveNamed(frame, name);
			if (found is not null) return found;

			failure = $"'{name}' is not an argument or local in this frame.";
			return null;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Resolving a value path's root failed.");
			failure = exception.Message;
			return null;
		}
	}

	/// <summary>Matches a bare name against the frame's arguments and then its locals.</summary>
	private static CorDebugValue? ResolveNamed(CorDebugILFrame frame, string name)
	{
		var (module, token) = FrameIdentity(frame);
		var (isStatic, parameterNames) = token is { } methodToken && module is not null
			? MethodTokens.ParameterNames(module, methodToken)
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

	/// <summary>Reads one element of an array value, dereferencing to reach the array first.</summary>
	private static CorDebugValue? ResolveElement(CorDebugValue value, int index, out string? failure)
	{
		failure = null;
		var unwrapped = Unwrap(value);

		if (unwrapped is not CorDebugArrayValue array)
		{
			failure = $"[{index}] needs an array, and this value is not one.";
			return null;
		}

		if (index >= array.Count)
		{
			failure = $"[{index}] is past the end of an array of {array.Count}.";
			return null;
		}

		return array.GetElementAtPosition(index);
	}

	/// <summary>
	/// What is inside a value, with each child carrying the path that expands it in turn.
	/// <para>
	/// An object's fields are read from the exact type and every base above it, each level's fields
	/// through that level's own class -- <c>GetFieldValue</c> wants the class the field was declared
	/// on, not the value's. A base can live in another module, which is why the chain is walked
	/// through the runtime's types rather than read out of one module's metadata.
	/// </para>
	/// </summary>
	private (IReadOnlyList<LiveVariable> Children, int Total, bool Truncated) ChildrenOf(CorDebugValue value, string basePath)
	{
		var children = new List<LiveVariable>();
		var total = 0;

		try
		{
			var unwrapped = Unwrap(value);

			if (unwrapped is CorDebugArrayValue array)
			{
				total = array.Count;
				for (var i = 0; i < total && children.Count < MaxChildren; i++)
				{
					var (typeName, rendered, hasChildren) = ValueReader.Read(array.GetElementAtPosition(i));
					children.Add(new LiveVariable
					{
						Name = $"[{i}]",
						Kind = "element",
						TypeName = typeName,
						Value = rendered,
						Path = ValuePath.Element(basePath, i),
						HasChildren = hasChildren,
					});
				}

				return (children, total, children.Count < total);
			}

			if (unwrapped is not CorDebugObjectValue objectValue) return (children, 0, false);

			for (var type = ExactTypeOf(unwrapped); type is not null; type = BaseOf(type))
			{
				var cls = type.Class;
				var fields = MethodTokens.Fields(cls.Module.Name, cls.Token);

				foreach (var field in fields)
				{
					// Statics belong to the type rather than to the value in hand, and a caller
					// asking what an object holds has not asked about its type.
					if (field.IsStatic) continue;

					total++;
					if (children.Count >= MaxChildren) continue;

					children.Add(DescribeField(objectValue, cls, field, basePath));
				}
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Listing the children of '{Path}' failed.", basePath);
		}

		return (children, total, children.Count < total);
	}

	private static LiveVariable DescribeField(CorDebugObjectValue objectValue, CorDebugClass cls, FieldMember field, string basePath)
	{
		try
		{
			// GetFieldValue wants the raw ICorDebugClass; the ClrDebug wrapper is not it, and casting
			// the wrapper to the interface throws.
			var (typeName, rendered, hasChildren) = ValueReader.Read(objectValue.GetFieldValue(cls.Raw, field.Token));

			return new LiveVariable
			{
				Name = field.Name,
				Kind = "field",
				TypeName = typeName,
				Value = rendered,
				Path = ValuePath.Field(basePath, field.Name),
				HasChildren = hasChildren,
			};
		}
		catch (Exception)
		{
			// A field the runtime will not read -- a literal, or one optimised out -- is reported as
			// unreadable rather than dropped, so a reader is not left wondering where it went.
			return new LiveVariable
			{
				Name = field.Name,
				Kind = "field",
				Value = "(unreadable)",
				Path = ValuePath.Field(basePath, field.Name),
				HasChildren = false,
			};
		}
	}

	/// <summary>Follows a reference to what it points at, and a box to what it holds.</summary>
	private static CorDebugValue Unwrap(CorDebugValue value)
	{
		if (value is CorDebugReferenceValue reference && !reference.IsNull) value = reference.Dereference();
		if (value is CorDebugBoxValue box) value = box.Object;

		return value;
	}

	private static CorDebugType? ExactTypeOf(CorDebugValue value)
	{
		try
		{
			return value.ExactType;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static CorDebugType? BaseOf(CorDebugType type)
	{
		try
		{
			return type.Base;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>A thread's IL frames and whether the walk stopped at its own limit.</summary>
	private sealed record FrameWalk(IReadOnlyList<WalkedFrame> Frames, bool Truncated);

	/// <summary>One IL frame, with the count of unrepresentable frames immediately below it.</summary>
	private sealed record WalkedFrame(CorDebugILFrame Frame, int SkippedBefore);

	/// <summary>
	/// Evaluates a value path against the stopped frame (issue #7): an argument or local -- by name,
	/// or as <c>arg:0</c> / <c>local:2</c> -- then <c>.field</c> and <c>[3]</c> into the object graph,
	/// read directly from memory. It runs none of the debuggee's own code -- no property getters, no
	/// method calls -- so it cannot hang or corrupt the target; those need func-eval, a deliberate
	/// non-goal. Returns an error, not a throw, when nothing is stopped or the path does not resolve.
	/// <para>
	/// The same grammar and the same resolver as an expansion, so a path a caller was shown in a
	/// variable evaluates without translation, and the two cannot disagree about what one means.
	/// </para>
	/// </summary>
	public LiveEvaluation Evaluate(string expression)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint || _stoppedThread is null || _process is null || _detached || _exited)
			{
				return new LiveEvaluation { Expression = expression, Error = "The target is not stopped; evaluation needs a stop at a breakpoint or step." };
			}

			try
			{
				var path = ValuePath.Parse(expression);
				var frame = FindTopILFrame(_stoppedThread);
				if (frame is null)
				{
					return new LiveEvaluation { Expression = expression, Error = "The held thread has no managed frame to evaluate against." };
				}

				var value = Resolve(frame, path, out var failure);
				if (value is null) return new LiveEvaluation { Expression = expression, Error = failure };

				var (typeName, rendered, _) = ValueReader.Read(value);
				return new LiveEvaluation { Expression = expression, TypeName = typeName, Value = rendered };
			}
			catch (ArgumentException exception)
			{
				return new LiveEvaluation { Expression = expression, Error = exception.Message };
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Evaluating '{Expression}' failed.", expression);
				return new LiveEvaluation { Expression = expression, Error = exception.Message };
			}
		}
	}

	/// <summary>
	/// Reads a field off a value by name: dereference a reference, unwrap a box, then read the field
	/// directly.
	/// <para>
	/// The type chain is walked rather than the value's own class asked, because a field declared on
	/// a base is as much a part of the object -- and it is what an expansion lists, so a path taken
	/// from one has to resolve back here or the two surfaces disagree about what an object holds.
	/// </para>
	/// </summary>
	private static CorDebugValue? ResolveField(CorDebugValue value, string fieldName)
	{
		var unwrapped = Unwrap(value);
		if (unwrapped is not CorDebugObjectValue objectValue) return null;

		for (var type = ExactTypeOf(unwrapped); type is not null; type = BaseOf(type))
		{
			if (FieldOn(objectValue, type.Class, fieldName) is { } found) return found;
		}

		// A value whose exact type the runtime will not give still has a class, and for anything
		// without a base that is the whole answer.
		return FieldOn(objectValue, objectValue.Class, fieldName);
	}

	/// <summary>Reads a field declared on one level of a value's type chain, or null if it is not there.</summary>
	private static CorDebugValue? FieldOn(CorDebugObjectValue objectValue, CorDebugClass cls, string fieldName)
	{
		try
		{
			var token = MethodTokens.FieldToken(cls.Module.Name, (int)cls.Token, fieldName);
			if (token is null) return null;

			// GetFieldValue wants the raw ICorDebugClass; the ClrDebug wrapper is not it (casting the
			// wrapper to the interface throws), so hand over its Raw.
			return objectValue.GetFieldValue(cls.Raw, token.Value);
		}
		catch (Exception)
		{
			return null;
		}
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
	/// The top managed frame's arguments and locals, captured on the callback that announced a stop
	/// so the event stream carries them without a second call.
	/// <para>
	/// The same reader a frame request uses, so what a stop event says and what
	/// <c>rose_live_app_frame_variables</c> says about frame 0 cannot drift apart -- including the
	/// paths, which is what lets a caller expand a value it saw in an event.
	/// </para>
	/// </summary>
	private IReadOnlyList<LiveVariable> ReadTopFrameVariables(CorDebugThread thread)
	{
		try
		{
			var frame = FindTopILFrame(thread);
			if (frame is null) return [];

			return ReadVariables(frame).Variables;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading the stopped frame's variables failed.");
			return [];
		}
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
					RememberModule(module);

					foreach (var binding in _bindings)
					{
						if (!binding.Bound) TryBind(binding, module);
					}
				}

				// This walk is the one a name search would otherwise have to take for itself.
				_modulesEnumerated = true;

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

	/// <summary>
	/// Notes a module's file, so its metadata and symbols can be read without touching the target
	/// again. A dynamic or in-memory module is skipped: there is no file to read it out of.
	/// </summary>
	private void RememberModule(CorDebugModule module)
	{
		string path;

		try
		{
			if (module.IsDynamic || module.IsInMemory) return;

			path = module.Name;
		}
		catch (Exception)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(path)) return;

		lock (_gate)
		{
			if (!_modulePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _modulePaths.Add(path);
		}
	}

	/// <summary>
	/// The target's loaded module files, walking the ones that predate this session's attach the
	/// first time anybody asks.
	/// </summary>
	private IReadOnlyList<string> ModulePaths()
	{
		lock (_gate)
		{
			if (!_modulesEnumerated) EnumerateLoadedModules();

			return [.. _modulePaths];
		}
	}

	/// <summary>
	/// Walks the target's modules once, async-breaking it to a synchronized state to do so.
	/// <para>
	/// The stop and the continue are a pair, which is what makes this safe to call while the target
	/// is held at a breakpoint: the stop count goes up and back down and the target stays exactly as
	/// stopped as it was.
	/// </para>
	/// </summary>
	private void EnumerateLoadedModules()
	{
		if (_process is null || _detached || _exited) return;

		var stopped = false;
		try
		{
			_process.Stop(0);
			stopped = true;

			foreach (var module in EnumerateModules(_process))
			{
				RememberModule(module);
			}

			_modulesEnumerated = true;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Enumerating the target's loaded modules failed.");
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
					logger.LogDebug(exception, "Continue after enumerating modules failed.");
				}
			}
		}
	}

	/// <summary>
	/// Takes in a module as it loads: notes its file, and binds anything waiting for it. Called from
	/// a stopped callback.
	/// </summary>
	private void BindModule(CorDebugModule module)
	{
		// Before the early return below, because the list of modules is wanted by a name search
		// whether or not anything is waiting to bind.
		RememberModule(module);

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

		var offset = binding.Location.IlOffset;

		try
		{
			var function = module.GetFunctionFromToken(token.Value);

			// A named method binds at its first instruction; a picked position binds inside the IL,
			// which is the only way to stop on a line that is not the method's first.
			var breakpoint = offset is { } instruction
				? function.ILCode.CreateBreakpoint(instruction)
				: function.CreateBreakpoint();

			breakpoint.Activate(true);

			binding.Breakpoint = breakpoint;
			binding.Token = token.Value;
			binding.ModulePath = module.Name;
			binding.Source = SourceAt(module.Name, token.Value, offset ?? 0);
			binding.Detail = null;
			buffer.Append(LiveDebugEventKind.SessionNotice, $"{binding.Id} bound at {binding.Raw}.");
			logger.LogInformation("Binding {Id} bound at {Location} (token 0x{Token:x8}).", binding.Id, binding.Raw, token.Value);
		}
		catch (Exception exception)
		{
			// An offset the method's IL does not contain is the failure worth naming apart. It comes
			// back as an HRESULT about setting a breakpoint, which says nothing about the number
			// being wrong, and it is the one thing a caller composing a location by hand gets wrong.
			binding.Detail = offset is { } bad
				? $"bind failed at IL_{bad:X4}: {exception.Message}. The offset must be one this method's symbols report."
				: $"bind failed: {exception.Message}";
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
		IlOffset = binding.Location.IlOffset,
		Source = binding.Source,
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
		IlOffset = binding.Location.IlOffset,
		Source = binding.Source,
		HitCount = binding.HitCount,
		AutoContinueSeconds = binding.AutoContinueSeconds ?? DefaultAutoContinueSeconds,
		Condition = binding.ConditionText,
		Detail = binding.Bound ? null : binding.Detail,
	};

	private static LiveMethodMatch DescribeMatch(MethodCandidate candidate) => new()
	{
		Location = candidate.Location,
		DisplayName = candidate.DisplayName,
		Signature = candidate.Signature,
		Module = candidate.Module,
		HasSymbols = candidate.HasSymbols,
	};

	/// <summary>
	/// What a reader needs to know about a search that the list of matches does not say. Null when
	/// the answer speaks for itself, since a caption that is always there is one nobody reads.
	/// </summary>
	private static string? SearchDetail(string query, int modules, MethodSearchResult found)
	{
		if (!MethodQuery.IsWorthSearching(query))
		{
			return $"Type at least {MethodQuery.ShortestQuery} characters. A shorter query matches most "
				+ "of a framework, and reading every loaded module to say so is the cost of the answer.";
		}

		if (modules == 0)
		{
			return "No modules are known yet. The target reports them as it loads them, so this fills in "
				+ "once it is running.";
		}

		return found.ModulesUnreadable > 0
			? $"{found.ModulesUnreadable} of {modules} loaded modules were not searched: a native library "
				+ "or one built in memory has no metadata on disk to read."
			: null;
	}

	/// <summary>
	/// Every place in a region where execution can stop, in source order, each naming the method its
	/// instructions belong to rather than the one somebody was reading.
	/// </summary>
	private static IReadOnlyList<LiveMethodPosition> PositionsIn(
		IReadOnlyList<MethodExtent> region,
		string modulePath,
		string module,
		string file)
	{
		var positions = new List<LiveMethodPosition>();

		foreach (var extent in region)
		{
			var parts = MethodTokens.MethodParts(modulePath, extent.MethodToken);
			var owner = parts is { } named ? $"{module}!{named.TypeName}.{named.MethodName}" : null;
			var label = parts is { } shown ? MethodDisplayName.Of(shown.TypeName, shown.MethodName) : null;

			// A method whose metadata will not name it cannot be addressed, so its lines are not
			// offered. Offering a position nothing can be set at is worse than leaving the line plain.
			if (owner is null || label is null) continue;

			foreach (var point in extent.Points)
			{
				if (point.Position is not { } at) continue;
				if (!string.Equals(at.File, file, StringComparison.OrdinalIgnoreCase)) continue;

				positions.Add(new LiveMethodPosition
				{
					Location = $"{owner}@IL_{point.Offset:X4}",
					DisplayName = label,
					IlOffset = point.Offset,
					Line = at.Line,
					Column = at.Column,
					EndLine = at.EndLine,
					EndColumn = at.EndColumn,
				});
			}
		}

		return [.. positions.OrderBy(position => position.Line).ThenBy(position => position.Column)];
	}

	/// <summary>A method that cannot be shown, saying why. Its first instruction is still breakable at.</summary>
	private static LiveMethodSource NoMethodSource(
		string location,
		string displayName,
		string module,
		LiveSymbolState symbols,
		string detail) => new()
		{
			Location = location,
			DisplayName = displayName,
			Module = module,
			Symbols = symbols,
			FirstLine = 0,
			Lines = [],
			Positions = [],
			Detail = detail,
		};

	/// <summary>Where an instruction came from in source, or null when the symbols cannot say.</summary>
	private static LiveSourcePosition? SourceAt(string modulePath, int methodToken, int ilOffset)
	{
		if (SymbolCache.Shared.For(modulePath)?.Pdb?.Position(methodToken, ilOffset) is not { } at) return null;

		return new LiveSourcePosition
		{
			File = at.File,
			Line = at.Line,
			Column = at.Column,
			EndLine = at.EndLine,
			EndColumn = at.EndColumn,
		};
	}

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

		/// <summary>The module file it bound in, for reading the symbols that say where that was.</summary>
		public string? ModulePath { get; set; }

		/// <summary>
		/// Where in source it bound, read once at bind rather than on each listing: a listing is
		/// polled while a panel is open and the answer cannot change while the module is loaded.
		/// </summary>
		public LiveSourcePosition? Source { get; set; }

		public CorDebugFunctionBreakpoint? Breakpoint { get; set; }

		public string? Detail { get; set; }

		public bool Bound => Breakpoint is not null;
	}
}

/// <summary>The process exists but its CoreCLR has not loaded yet; the caller may retry.</summary>
internal sealed class RuntimeNotReadyException(int pid)
	: Exception($"pid {pid} has no CoreCLR loaded yet.");
