using System.Runtime.InteropServices;

using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// One ICorDebug session against one already-running process. Callbacks arrive on mscordbi's own
/// thread with the debuggee stopped, and nothing in it runs again until <c>Continue</c> is called,
/// so every handler ends by recording the event and continuing -- except ExitProcess, after which
/// there is nothing left to continue.
/// <para>
/// This is the attach half of issue #4: it never launches a process, so a running .NET target can be
/// attached to and watched with nothing injected into it. It also supports tracepoints (issue #17) --
/// breakpoints that log and auto-continue, never pausing the target. Events are captured into a
/// <see cref="DebugEventBuffer"/> rather than printed, because the reader is a turn-based agent that
/// looks between its turns.
/// </para>
/// </summary>
internal sealed class CorDebugSession(DebugEventBuffer buffer, ILogger logger) : IDisposable
{
	private static readonly TimeSpan RuntimeReadyTimeout = TimeSpan.FromSeconds(5);

	private readonly Lock _gate = new();
	private readonly List<Tracepoint> _tracepoints = [];
	private int _nextTracepointId = 1;

	private DbgShim? _shim;
	private CorDebug? _corDebug;
	private CorDebugProcess? _process;
	private bool _detached;
	private volatile bool _exited;

	public int? TargetProcessId { get; private set; }

	public bool HasExited => _exited;

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
	/// Adds a tracepoint, binding it immediately if its module is already loaded and otherwise when
	/// the module loads. A tracepoint never pauses the target: each hit is logged and continued.
	/// </summary>
	public LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit)
	{
		var parsed = SymbolLocation.Parse(location);
		if (logEveryNthHit is < 1) throw new ArgumentException("logEveryNthHit must be at least 1.");

		Tracepoint tracepoint;
		lock (_gate)
		{
			tracepoint = new Tracepoint
			{
				Id = $"tp-{_nextTracepointId++}",
				Location = parsed,
				Raw = location,
				LogMessage = logMessage,
				LogEveryNthHit = logEveryNthHit,
				Detail = "module not loaded yet",
			};
			_tracepoints.Add(tracepoint);
		}

		BindAgainstLoadedModules();

		lock (_gate)
		{
			return Describe(tracepoint);
		}
	}

	public IReadOnlyList<LiveTracepoint> ListTracepoints()
	{
		lock (_gate)
		{
			return [.. _tracepoints.Select(Describe)];
		}
	}

	public bool RemoveTracepoint(string id)
	{
		lock (_gate)
		{
			var tracepoint = _tracepoints.FirstOrDefault(entry => entry.Id == id);
			if (tracepoint is null) return false;

			try
			{
				tracepoint.Breakpoint?.Activate(false);
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Deactivating tracepoint {Id} failed.", id);
			}

			_tracepoints.Remove(tracepoint);
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
		try
		{
			Record(e);
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

	private void Record(CorDebugManagedCallbackEventArgs e)
	{
		switch (e)
		{
			case CreateProcessCorDebugManagedCallbackEventArgs created:
				// Debugger.Log in the debuggee only produces LogMessage events once this is on.
				created.Process.EnableLogMessages(true);
				buffer.Append(LiveDebugEventKind.ProcessCreated, $"Process {created.Process.Id} reported to the debugger.");
				break;

			case LoadModuleCorDebugManagedCallbackEventArgs loaded:
				buffer.Append(LiveDebugEventKind.ModuleLoaded, $"Loaded {loaded.Module.Name}", moduleName: loaded.Module.Name);
				BindModule(loaded.Module);
				break;

			case LogMessageCorDebugManagedCallbackEventArgs log:
				buffer.Append(
					LiveDebugEventKind.LogMessage,
					$"[{log.LogSwitchName}] {log.Message.TrimEnd()}",
					threadId: TryThreadId(log.Thread));
				break;

			case BreakpointCorDebugManagedCallbackEventArgs hit:
				RecordBreakpointHit(hit);
				break;

			case Exception2CorDebugManagedCallbackEventArgs exception:
				RecordException(exception);
				break;

			case ExitProcessCorDebugManagedCallbackEventArgs:
				buffer.Append(LiveDebugEventKind.ProcessExited, "The target process exited.");
				break;

			// Thread churn and the rest are continued but not buffered: high volume, low signal for
			// the first dogfood, which is exceptions and log output.
			default:
				break;
		}
	}

	private void RecordException(Exception2CorDebugManagedCallbackEventArgs exception)
	{
		// Catch-handler-found is bookkeeping that follows a first-chance throw; skip it as noise.
		if (exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND) return;

		var unhandled = exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED;
		var kind = unhandled ? LiveDebugEventKind.ExceptionUnhandled : LiveDebugEventKind.ExceptionFirstChance;
		var typeName = DescribeExceptionType(exception.Thread);

		buffer.Append(
			kind,
			$"{(unhandled ? "Unhandled" : "First-chance")} {typeName} on thread {TryThreadId(exception.Thread)?.ToString() ?? "?"}",
			threadId: TryThreadId(exception.Thread),
			exceptionType: typeName);
	}

	private void RecordBreakpointHit(BreakpointCorDebugManagedCallbackEventArgs hit)
	{
		var threadId = TryThreadId(hit.Thread);
		var (token, moduleName) = TryFunctionIdentity(hit.Breakpoint as CorDebugFunctionBreakpoint);

		Tracepoint? tracepoint;
		long ordinal;
		lock (_gate)
		{
			tracepoint = _tracepoints.FirstOrDefault(entry =>
				entry.Bound
				&& entry.Token == token
				&& (moduleName is null
					|| string.Equals(Path.GetFileNameWithoutExtension(moduleName), entry.Location.ModuleSimpleName, StringComparison.OrdinalIgnoreCase)));

			// With one tracepoint bound, an unidentified hit is unambiguously it.
			tracepoint ??= _tracepoints.Count(entry => entry.Bound) == 1 ? _tracepoints.First(entry => entry.Bound) : null;
			ordinal = tracepoint is null ? 0 : ++tracepoint.HitCount;
		}

		// A hit-count filter still counts every hit; it only thins what is logged.
		if (tracepoint?.LogEveryNthHit is { } nth && ordinal % nth != 0) return;

		var location = tracepoint?.Raw ?? "unknown location";
		var suffix = tracepoint?.LogMessage is { Length: > 0 } message ? $": {message}" : string.Empty;
		buffer.Append(
			LiveDebugEventKind.BreakpointHit,
			$"Tracepoint {location} hit #{ordinal} on thread {threadId?.ToString() ?? "?"}{suffix}",
			threadId: threadId);
	}

	/// <summary>
	/// Binds any unbound tracepoints against modules already loaded when the tracepoint was added. It
	/// async-breaks the target to a synchronized state to enumerate its modules, then resumes it; a
	/// tracepoint whose module has not loaded yet stays unbound and binds later on the load callback.
	/// </summary>
	private void BindAgainstLoadedModules()
	{
		lock (_gate)
		{
			if (_process is null || _detached || _exited) return;
			if (_tracepoints.TrueForAll(tracepoint => tracepoint.Bound)) return;

			var stopped = false;
			try
			{
				_process.Stop(0);
				stopped = true;

				foreach (var module in EnumerateModules(_process))
				{
					foreach (var tracepoint in _tracepoints)
					{
						if (!tracepoint.Bound) TryBind(tracepoint, module);
					}
				}
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Binding tracepoints against loaded modules failed.");
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
						logger.LogDebug(exception, "Continue after tracepoint bind failed.");
					}
				}
			}
		}
	}

	/// <summary>Binds unbound tracepoints against a module as it loads; called from a stopped callback.</summary>
	private void BindModule(CorDebugModule module)
	{
		lock (_gate)
		{
			if (_tracepoints.TrueForAll(tracepoint => tracepoint.Bound)) return;

			foreach (var tracepoint in _tracepoints)
			{
				if (!tracepoint.Bound) TryBind(tracepoint, module);
			}
		}
	}

	private void TryBind(Tracepoint tracepoint, CorDebugModule module)
	{
		if (tracepoint.Bound) return;

		try
		{
			if (module.IsDynamic || module.IsInMemory) return;
		}
		catch (Exception)
		{
			return; // A module that cannot describe itself is not one we can read metadata from.
		}

		var simpleName = Path.GetFileNameWithoutExtension(module.Name);
		if (!string.Equals(simpleName, tracepoint.Location.ModuleSimpleName, StringComparison.OrdinalIgnoreCase)) return;

		var token = MethodTokens.Find(module.Name, tracepoint.Location.TypeName, tracepoint.Location.MethodName);
		if (token is null)
		{
			tracepoint.Detail = $"no method {tracepoint.Location.TypeName}.{tracepoint.Location.MethodName} in {Path.GetFileName(module.Name)}";
			return;
		}

		try
		{
			var function = module.GetFunctionFromToken(token.Value);
			var breakpoint = function.CreateBreakpoint();
			breakpoint.Activate(true);

			tracepoint.Breakpoint = breakpoint;
			tracepoint.Token = token.Value;
			tracepoint.Detail = null;
			buffer.Append(LiveDebugEventKind.SessionNotice, $"Tracepoint {tracepoint.Id} bound at {tracepoint.Raw}.");
			logger.LogInformation("Tracepoint {Id} bound at {Location} (token 0x{Token:x8}).", tracepoint.Id, tracepoint.Raw, token.Value);
		}
		catch (Exception exception)
		{
			tracepoint.Detail = $"bind failed: {exception.Message}";
			logger.LogDebug(exception, "Binding tracepoint {Id} at {Location} failed.", tracepoint.Id, tracepoint.Raw);
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

	private static LiveTracepoint Describe(Tracepoint tracepoint) => new()
	{
		Id = tracepoint.Id,
		Location = tracepoint.Raw,
		Bound = tracepoint.Bound,
		HitCount = tracepoint.HitCount,
		LogMessage = tracepoint.LogMessage,
		LogEveryNthHit = tracepoint.LogEveryNthHit,
		Detail = tracepoint.Bound ? null : tracepoint.Detail,
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

	private sealed class Tracepoint
	{
		public required string Id { get; init; }

		public required SymbolLocation Location { get; init; }

		/// <summary>The location as the caller wrote it, for reporting.</summary>
		public required string Raw { get; init; }

		public string? LogMessage { get; init; }

		public int? LogEveryNthHit { get; init; }

		public long HitCount { get; set; }

		/// <summary>The bound method's metadata token, used to match a hit back to this tracepoint.</summary>
		public int? Token { get; set; }

		public CorDebugFunctionBreakpoint? Breakpoint { get; set; }

		public string? Detail { get; set; }

		public bool Bound => Breakpoint is not null;
	}
}

/// <summary>The process exists but its CoreCLR has not loaded yet; the caller may retry.</summary>
internal sealed class RuntimeNotReadyException(int pid)
	: Exception($"pid {pid} has no CoreCLR loaded yet.");
