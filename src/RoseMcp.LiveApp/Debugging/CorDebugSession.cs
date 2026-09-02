using System.Runtime.InteropServices;

using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// One ICorDebug session against one already-running process. Callbacks arrive on mscordbi's own
/// thread with the debuggee stopped, and nothing in it runs again until <c>Continue</c> is called, so
/// every handler ends by recording the event and continuing -- except ExitProcess, after which there
/// is nothing left to continue, and a stopping breakpoint, which deliberately holds.
/// <para>
/// This is the attach half of issue #4: it never launches a process, so a running .NET target can be
/// attached to and watched with nothing injected into it. It supports both tracepoints (#17) --
/// breakpoints that log and auto-continue, never pausing -- and stopping breakpoints (#6), which hold
/// the target and notify, with a safety timeout so an unattended stop cannot wedge the app. Events
/// are captured into a <see cref="DebugEventBuffer"/> rather than printed, because the reader is a
/// turn-based agent that looks between its turns.
/// </para>
/// </summary>
internal sealed class CorDebugSession(DebugEventBuffer buffer, ILogger logger) : IDisposable
{
	private static readonly TimeSpan RuntimeReadyTimeout = TimeSpan.FromSeconds(5);
	private const int MaxStackFrames = 20;
	private const int MaxVariables = 64;
	private const int DefaultAutoContinueSeconds = 30;

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
	public void Attach(int pid)
	{
		var shim = LoadDbgShim();
		var runtime = FindRuntimeWithRetry(shim, pid);
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

	public void Detach()
	{
		lock (_gate)
		{
			if (_process is null || _detached || _exited) return;

			DisposeTimer();
			_stoppedAtBreakpoint = false;
			_stoppedThread = null;

			try
			{
				// Detach needs a stopped process; stopping and detaching leaves the target running.
				_process.Stop(0);
				_process.Detach();
				_detached = true;
				buffer.Append(LiveDebugEventKind.SessionNotice, "Detached; the target keeps running.");
				logger.LogInformation("Detached from pid {Pid}.", TargetProcessId);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Detach from pid {Pid} failed.", TargetProcessId);
			}
		}
	}

	/// <summary>
	/// Adds a tracepoint: a breakpoint that logs and auto-continues, never pausing the target. It binds
	/// immediately if its module is already loaded and otherwise when the module loads.
	/// </summary>
	public LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit)
	{
		if (logEveryNthHit is < 1) throw new ArgumentException("logEveryNthHit must be at least 1.");

		var binding = AddBinding(location, stopOnHit: false, logMessage, logEveryNthHit, autoContinueSeconds: null);
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
	public LiveBreakpoint AddBreakpoint(string location, int? autoContinueSeconds)
	{
		if (autoContinueSeconds is < 1) throw new ArgumentException("autoContinueSeconds must be at least 1.");

		var binding = AddBinding(location, stopOnHit: true, logMessage: null, logEveryNthHit: null, autoContinueSeconds);
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
	/// Resumes a target held at a stopping breakpoint. Returns false when nothing was stopped. The
	/// safety timer calls the same path, so whichever comes first resumes and the other is a no-op.
	/// </summary>
	public bool Continue() => ContinueInternal(auto: false);

	/// <summary>
	/// Steps the held thread: <c>in</c> into calls, <c>over</c> them, or <c>out</c> of the current
	/// frame. It resumes the target so the step runs; a StepComplete callback then holds it again at
	/// the new location. Returns false when nothing is currently stopped.
	/// </summary>
	public bool Step(string mode)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint || _stoppedThread is null || _process is null || _detached || _exited) return false;

			try
			{
				var stepper = _stoppedThread.CreateStepper();
				switch (mode.Trim().ToLowerInvariant())
				{
					case "out":
						stepper.StepOut();
						break;
					case "in":
						stepper.Step(bStepIn: true);
						break;
					default: // "over"
						stepper.Step(bStepIn: false);
						break;
				}
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Issuing a {Mode} step failed.", mode);
				return false;
			}

			// Resume so the step executes; the StepComplete callback holds the target again.
			_stoppedAtBreakpoint = false;
			_stoppedBindingId = null;
			_stoppedThread = null;
			DisposeTimer();

			try
			{
				_process.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Continuing for a step failed.");
				return false;
			}

			return true;
		}
	}

	public void Dispose()
	{
		Detach();
		try
		{
			// Terminates the debugging interface, not the debuggee -- the process was detached above.
			_corDebug?.Terminate();
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Terminating the ICorDebug interface failed.");
		}
	}

	private BreakpointBinding AddBinding(string location, bool stopOnHit, string? logMessage, int? logEveryNthHit, int? autoContinueSeconds)
	{
		var parsed = SymbolLocation.Parse(location);

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
				Detail = "module not loaded yet",
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

	private bool ContinueInternal(bool auto)
	{
		lock (_gate)
		{
			if (!_stoppedAtBreakpoint || _process is null || _detached || _exited) return false;

			_stoppedAtBreakpoint = false;
			var id = _stoppedBindingId;
			_stoppedBindingId = null;
			_stoppedThread = null;
			DisposeTimer();

			try
			{
				_process.Continue(fIsOutOfBand: false);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Continuing from a stop failed.");
				return false;
			}

			var where = id is null ? "a step" : $"breakpoint {id}";
			buffer.Append(
				LiveDebugEventKind.SessionNotice,
				auto ? $"Auto-continued from {where} after the safety timeout." : $"Continued from {where}.");
			return true;
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
	private RuntimeInProcess FindRuntimeWithRetry(DbgShim shim, int pid)
	{
		var deadline = DateTime.UtcNow + RuntimeReadyTimeout;
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
			DisposeTimer();
			_autoContinueTimer = new Timer(_ => ContinueInternal(auto: true), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);

			buffer.Append(
				kind,
				$"{prefix} at {top} on thread {threadId?.ToString() ?? "?"} -- stopped; continue or step (auto-continues in {seconds}s).",
				threadId: threadId,
				frames: frames.Count > 0 ? frames : null,
				variables: variables.Count > 0 ? variables : null);
		}

		return false;
	}

	/// <summary>
	/// The top managed frame's arguments and locals, read while the thread is stopped. Argument names
	/// come from metadata (an instance method's argument 0 is <c>this</c>); local names need a PDB and
	/// are indexed when one is not available. Reading is defensive per variable, so one unreadable
	/// value does not lose the rest of the frame.
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

			var locals = frame.EnumerateLocalVariables().ToList();
			for (var i = 0; i < locals.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value) = ValueReader.Read(locals[i]);
				variables.Add(new LiveVariable { Name = $"local_{i}", Kind = "local", TypeName = typeName, Value = value });
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

				foreach (var module in EnumerateModules(_process))
				{
					foreach (var binding in _bindings)
					{
						if (!binding.Bound) TryBind(binding, module);
					}
				}
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

		var simpleName = Path.GetFileNameWithoutExtension(module.Name);
		if (!string.Equals(simpleName, binding.Location.ModuleSimpleName, StringComparison.OrdinalIgnoreCase)) return;

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

	private void DisposeTimer()
	{
		_autoContinueTimer?.Dispose();
		_autoContinueTimer = null;
	}

	private static LiveTracepoint DescribeTracepoint(BreakpointBinding binding) => new()
	{
		Id = binding.Id,
		Location = binding.Raw,
		Bound = binding.Bound,
		HitCount = binding.HitCount,
		LogMessage = binding.LogMessage,
		LogEveryNthHit = binding.LogEveryNthHit,
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
