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
/// This is the attach half of issue #4: it never launches a process and sets no breakpoints, so a
/// running .NET target can be attached to and watched with nothing injected into it. Events are
/// captured into a <see cref="DebugEventBuffer"/> rather than printed, because the reader is a
/// turn-based agent that looks between its turns.
/// </para>
/// </summary>
internal sealed class CorDebugSession(DebugEventBuffer buffer, ILogger logger) : IDisposable
{
	private static readonly TimeSpan RuntimeReadyTimeout = TimeSpan.FromSeconds(5);

	private readonly Lock _gate = new();

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
				break;

			case LogMessageCorDebugManagedCallbackEventArgs log:
				buffer.Append(
					LiveDebugEventKind.LogMessage,
					$"[{log.LogSwitchName}] {log.Message.TrimEnd()}",
					threadId: TryThreadId(log.Thread));
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
}

/// <summary>The process exists but its CoreCLR has not loaded yet; the caller may retry.</summary>
internal sealed class RuntimeNotReadyException(int pid)
	: Exception($"pid {pid} has no CoreCLR loaded yet.");
