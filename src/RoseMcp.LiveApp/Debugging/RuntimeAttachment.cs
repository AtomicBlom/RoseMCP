using System.Runtime.InteropServices;

using ClrDebug;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Getting an ICorDebug interface onto a process: finding dbgshim, finding the runtime inside the
/// target, creating the debugging interface against it, and handing back the process to debug.
/// <para>
/// Which mscordbi talks to a target is decided by the coreclr that target is running, not by
/// anything this host was built against, so all of this is discovery rather than configuration. It
/// is the one part of a session that can fail before there is a session at all, and every failure
/// here is a message about the process rather than about the debugger.
/// </para>
/// <para>
/// This owns no session state and takes no lock. It is used once, at the start, and the session
/// holds what it produced.
/// </para>
/// </summary>
internal sealed class RuntimeAttachment(DebugEventBuffer buffer, ILogger logger)
{
	/// <summary>
	/// How long to wait for a freshly started process's CoreCLR to appear. A pid exists before its
	/// runtime loads, so the first look can legitimately find none.
	/// </summary>
	internal static readonly TimeSpan RuntimeReadyTimeout = TimeSpan.FromSeconds(5);

	/// <summary>How long a launched process has to signal that its runtime has started.</summary>
	internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

	private DbgShim? _shim;

	/// <summary>The debugging interface, or null before anything has been attached to.</summary>
	internal CorDebug? Interface { get; private set; }

	/// <summary>The process being debugged, or null before an attach has succeeded.</summary>
	internal CorDebugProcess? Process { get; private set; }

	/// <summary>The pid attached to, which outlives the process object for reporting.</summary>
	internal int? ProcessId { get; private set; }

	/// <summary>
	/// Attaches to a running process, waiting briefly for its runtime if it has only just started.
	/// Throws with a plain message when the target is not a debuggable .NET process.
	/// </summary>
	internal void Attach(int pid, TimeSpan runtimeReadyTimeout, EventHandler<CorDebugManagedCallbackEventArgs> onEvent)
	{
		var shim = LoadDbgShim();
		var runtime = FindRuntimeWithRetry(shim, pid, runtimeReadyTimeout);
		try
		{
			Interface = CreateCorDebug(shim, pid, runtime.Path, onEvent);
			Process = Interface.DebugActiveProcess(pid, win32Attach: false);
			ProcessId = pid;
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
	internal void Launch(string executablePath, string? arguments, EventHandler<CorDebugManagedCallbackEventArgs> onEvent)
	{
		var shim = LoadDbgShim();
		var commandLine = string.IsNullOrWhiteSpace(arguments) ? $"\"{executablePath}\"" : $"\"{executablePath}\" {arguments}";
		var workingDirectory = Path.GetDirectoryName(executablePath);
		var launched = shim.CreateProcessForLaunch(commandLine, bSuspendProcess: true, IntPtr.Zero, workingDirectory);
		try
		{
			AttachAtSuspendedStartup(shim, launched.ProcessId, () => shim.ResumeProcess(launched.ResumeHandle), StartupTimeout, onEvent);
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
	internal void AttachUwpAtStartup(
		int pid,
		Action resume,
		TimeSpan startupTimeout,
		EventHandler<CorDebugManagedCallbackEventArgs> onEvent)
	{
		var shim = LoadDbgShim();
		AttachAtSuspendedStartup(shim, pid, resume, startupTimeout, onEvent);
	}

	/// <summary>
	/// Terminates the debugging interface. Its documented precondition is that every process has been
	/// detached from or terminated, so the caller decides whether that holds.
	/// </summary>
	internal void Terminate()
	{
		try
		{
			Interface?.Terminate();
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Terminating the ICorDebug interface failed.");
		}
	}

	/// <summary>
	/// The shared startup-attach dance: arm the runtime-startup notification while the process is still
	/// suspended before its CLR has loaded, trigger the caller's <paramref name="resume"/>, wait for the
	/// runtime to signal, attach to it, and release it. The ordering is the whole trick -- the
	/// notification must be armed before the process is resumed, or the runtime can start before the
	/// debugger is listening and the startup is missed.
	/// </summary>
	private void AttachAtSuspendedStartup(
		DbgShim shim,
		int pid,
		Action resume,
		TimeSpan startupTimeout,
		EventHandler<CorDebugManagedCallbackEventArgs> onEvent)
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
			Interface = CreateCorDebug(shim, pid, runtime.Path, onEvent);
			Process = Interface.DebugActiveProcess(pid, win32Attach: false);
			ProcessId = pid;

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
	private static RuntimeInProcess FindRuntimeWithRetry(DbgShim shim, int pid, TimeSpan runtimeReadyTimeout)
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

	private CorDebug CreateCorDebug(
		DbgShim shim,
		int pid,
		string runtimePath,
		EventHandler<CorDebugManagedCallbackEventArgs> onEvent)
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
		callback.OnAnyEvent += onEvent;
		created.SetManagedHandler(callback);
		return created;
	}

	/// <summary>Wraps a native event handle so it can be waited on or set through the BCL.</summary>
	private static EventWaitHandle WrapEvent(IntPtr handle, bool ownsHandle)
	{
		var wrapped = new EventWaitHandle(false, EventResetMode.AutoReset);
		wrapped.SafeWaitHandle.Dispose();
		wrapped.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle);
		return wrapped;
	}

	private sealed record RuntimeInProcess(string Path, IntPtr Handle, EnumerateCLRsResult Enumeration);
}
