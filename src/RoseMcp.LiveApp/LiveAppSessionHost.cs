using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;
using RoseMcp.LiveApp.Xaml;

namespace RoseMcp.LiveApp;

/// <summary>
/// Owns the one target this host was launched for. For an attach it establishes a real ICorDebug
/// session over the target (issue #4, attach path) and captures its debug events into a buffer the
/// broker reads (issue #8). The XAML provider and the launch paths come in later issues; this is the
/// shell the broker supervises and everything else plugs into.
/// </summary>
public sealed class LiveAppSessionHost(LiveAppOptions options, ILogger<LiveAppSessionHost> logger) : IHostedService
{
	private static readonly TimeSpan UwpRuntimeReadyTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan UwpStartupTimeout = TimeSpan.FromSeconds(30);

	private readonly Lock _gate = new();
	private readonly DebugEventBuffer _events = new();
	private LiveAppSessionState _state = LiveAppSessionState.Starting;
	private int? _targetProcessId;
	private string? _detail;
	private CorDebugSession? _session;
	private string? _uwpPackageFullName;
	private XamlDiagnosticsSession? _xaml;

	/// <summary>The architecture this host launched as, which is the target's architecture.</summary>
	public static TargetArchitecture Architecture => RuntimeInformation.ProcessArchitecture switch
	{
		System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
		System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
		System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.Arm64,
		_ => TargetArchitecture.Unknown,
	};

	public LiveAppInfo CurrentInfo()
	{
		lock (_gate)
		{
			return new LiveAppInfo
			{
				HostProcessId = Environment.ProcessId,
				Architecture = Architecture,
				State = EffectiveState(),
				TargetProcessId = _targetProcessId,
				Detail = _detail,
			};
		}
	}

	/// <summary>Adds a tracepoint to the attached target.</summary>
	public LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit, string? condition)
		=> RequireSession().AddTracepoint(location, logMessage, logEveryNthHit, condition);

	public LiveTracepointList ListTracepoints()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveTracepointList { Tracepoints = session?.ListTracepoints() ?? [] };
	}

	public LiveTracepointList RemoveTracepoint(string id)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		session?.RemoveTracepoint(id);
		return new LiveTracepointList { Tracepoints = session?.ListTracepoints() ?? [] };
	}

	/// <summary>Sets a stopping breakpoint on the attached target.</summary>
	public LiveBreakpoint SetBreakpoint(string location, int? autoContinueSeconds, string? condition)
		=> RequireSession().AddBreakpoint(location, autoContinueSeconds, condition);

	public LiveBreakpointList ListBreakpoints()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveBreakpointList { Breakpoints = session?.ListBreakpoints() ?? [] };
	}

	public LiveBreakpointList RemoveBreakpoint(string id)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		session?.RemoveBreakpoint(id);
		return new LiveBreakpointList { Breakpoints = session?.ListBreakpoints() ?? [] };
	}

	/// <summary>Resumes a target held at a stopping breakpoint; false when nothing was stopped.</summary>
	public LiveContinueResult Continue()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveContinueResult { Continued = session?.Continue() == true };
	}

	/// <summary>Steps a target held at a breakpoint: "in", "over", or "out".</summary>
	public LiveContinueResult Step(string mode)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveContinueResult { Continued = session?.Step(mode) == true };
	}

	/// <summary>
	/// Injects the XAML diagnostics provider into the target and returns a snapshot of its live visual
	/// tree. Returns a tree carrying only a detail (no nodes) when the target has no XAML UI or the
	/// provider is unavailable, rather than throwing.
	/// </summary>
	public LiveXamlTree ReadXamlTree()
	{
		int? targetProcessId;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlTree { Detail = "This session has no target process to inspect." };
		}

		return _xaml.ReadTree(pid);
	}

	/// <summary>
	/// Reads one element's XAML properties (by the handle a tree snapshot reported) with provenance and,
	/// when the app carries source info, source location. Set properties only by default; the framework
	/// defaults are included on request.
	/// </summary>
	public LiveXamlProperties ReadXamlProperties(ulong handle, bool includeDefaults)
	{
		int? targetProcessId;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlProperties { Handle = handle, Detail = "This session has no target process to inspect." };
		}

		return _xaml.ReadProperties(pid, handle, includeDefaults);
	}

	/// <summary>A page of buffered debug events after the given cursor, with the session's state.</summary>
	public LiveDebugEventPage ReadEvents(long after)
	{
		var (events, nextCursor, oldest, total) = _events.ReadAfter(after);

		lock (_gate)
		{
			return new LiveDebugEventPage
			{
				State = EffectiveState(),
				NextCursor = nextCursor,
				OldestAvailable = oldest,
				TotalObserved = total,
				TargetProcessId = _targetProcessId,
				Events = events,
			};
		}
	}

	/// <summary>
	/// Detaches the debugger while the host is still alive, leaving the target running. The broker
	/// calls this before it closes the host's stdin: an ICorDebug debuggee whose debugger simply dies
	/// is taken down by the operating system, so the detach must complete first.
	/// </summary>
	public LiveAppInfo DetachTarget()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		session?.Detach();

		// For a UWP target, also lift the package's debug mode so it returns to its normal lifecycle.
		DisableUwpDebugging();

		lock (_gate)
		{
			if (_state == LiveAppSessionState.Ready) _state = LiveAppSessionState.Ended;
		}

		return CurrentInfo();
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Establish();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
			_session = null;
			_state = LiveAppSessionState.Ended;
		}

		session?.Dispose();
		DisableUwpDebugging();
		return Task.CompletedTask;
	}

	private void Establish()
	{
		switch (options.Target.Kind)
		{
			case LiveAppTargetKind.AttachProcess:
				EstablishAttach(options.Target.ProcessId);
				break;

			case LiveAppTargetKind.LaunchExecutable:
				EstablishLaunch(options.Target.ExecutablePath, options.Target.Arguments);
				break;

			case LiveAppTargetKind.LaunchUwp:
				EstablishUwp(options.Target.AppUserModelId);
				break;

			default:
				Fault($"{options.Target.Kind} is not implemented in this build.");
				break;
		}
	}

	private void EstablishAttach(int? processId)
	{
		if (processId is not { } pid)
		{
			Fault("An attach target needs a process id.");
			return;
		}

		try
		{
			using (var process = Process.GetProcessById(pid))
			{
				if (process.HasExited)
				{
					Fault($"Process {pid} has already exited.");
					return;
				}
			}
		}
		catch (ArgumentException)
		{
			Fault($"No process with id {pid} is running.");
			return;
		}

		var session = new CorDebugSession(_events, logger);
		try
		{
			session.Attach(pid);
		}
		catch (Exception exception)
		{
			session.Dispose();
			_events.Append(LiveDebugEventKind.SessionNotice, $"Attach failed: {exception.Message}");
			Fault(exception.Message);
			return;
		}

		lock (_gate)
		{
			_session = session;
			_targetProcessId = pid;
			_state = LiveAppSessionState.Ready;
			_detail = null;
		}

		logger.LogInformation("Live-app session established against pid {Pid} as {Architecture}.", pid, Architecture);
	}

	private void EstablishLaunch(string? executablePath, string? arguments)
	{
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			Fault("A launch target needs an executable path.");
			return;
		}

		if (!File.Exists(executablePath))
		{
			Fault($"No executable at {executablePath}.");
			return;
		}

		var session = new CorDebugSession(_events, logger);
		try
		{
			session.Launch(executablePath, arguments);
		}
		catch (Exception exception)
		{
			session.Dispose();
			_events.Append(LiveDebugEventKind.SessionNotice, $"Launch failed: {exception.Message}");
			Fault(exception.Message);
			return;
		}

		lock (_gate)
		{
			_session = session;
			_targetProcessId = session.TargetProcessId;
			_state = LiveAppSessionState.Ready;
			_detail = null;
		}

		logger.LogInformation("Live-app session launched {Path} as pid {Pid} ({Architecture}).", executablePath, session.TargetProcessId, Architecture);
	}

	/// <summary>
	/// Activates a packaged (UWP) app under the debugger. By default this is from birth (issue #5): the
	/// system creates the app suspended and launches a resume stub, so the debugger attaches before the
	/// runtime's first instruction and the whole of startup -- the first OnLaunched, its module loads,
	/// any exception it throws -- is captured. If the resume stub cannot be registered it falls back to
	/// attaching a beat after activation, which misses only that earliest window.
	/// </summary>
	private void EstablishUwp(string? appUserModelId)
	{
		if (string.IsNullOrWhiteSpace(appUserModelId))
		{
			Fault("A UWP target needs an app user-model id.");
			return;
		}

		var family = appUserModelId.Split('!', 2)[0];
		string packageFullName;
		try
		{
			packageFullName = Uwp.ResolvePackageFullName(family);
		}
		catch (Exception exception)
		{
			Fault(exception.Message);
			return;
		}

		var session = new CorDebugSession(_events, logger);
		int pid;
		try
		{
			pid = ActivateUwpUnderDebugger(session, appUserModelId, packageFullName);
		}
		catch (Exception exception)
		{
			session.Dispose();
			DisableUwpDebugging();
			_events.Append(LiveDebugEventKind.SessionNotice, $"UWP launch failed: {exception.Message}");
			Fault(exception.Message);
			return;
		}

		lock (_gate)
		{
			_session = session;
			_targetProcessId = pid;
			_state = LiveAppSessionState.Ready;
			_detail = null;
		}

		logger.LogInformation("Live-app session activated {Aumid} as pid {Pid} ({Architecture}).", appUserModelId, pid, Architecture);
	}

	/// <summary>
	/// Activates the app from birth: registers a resume stub as the package's debugger, so the system
	/// creates the app suspended and hands the stub its ids. Activation itself blocks until the app is
	/// resumed, so it runs on a background thread while the stub reports the ids, the session arms its
	/// runtime-startup notification, and only then the stub resumes the app -- the ordering that catches
	/// the runtime's first breath. Falls back to a post-startup attach when the stub command line is too
	/// long to register.
	/// </summary>
	private int ActivateUwpUnderDebugger(CorDebugSession session, string appUserModelId, string packageFullName)
	{
		using var coordinator = new UwpStartupCoordinator();
		var stubCommandLine = coordinator.TryBuildStubCommandLine();
		if (stubCommandLine is null)
		{
			_events.Append(LiveDebugEventKind.SessionNotice, "The resume-stub command line is too long to register; attaching after startup instead.");
			return ActivateUwpPostStartup(session, appUserModelId, packageFullName);
		}

		Uwp.EnableDebugging(packageFullName, stubCommandLine);
		lock (_gate)
		{
			_uwpPackageFullName = packageFullName;
		}

		_events.Append(LiveDebugEventKind.SessionNotice, $"Enabled debug mode with a startup resume stub on {packageFullName}.");

		coordinator.BeginActivation(appUserModelId);
		var (pid, tid) = coordinator.WaitForStub(UwpStartupTimeout);
		_events.Append(LiveDebugEventKind.SessionNotice, $"Resume stub reported pid {pid} (thread {tid}); attaching from birth.");

		session.AttachUwpAtStartup(pid, coordinator.Resume, UwpStartupTimeout);
		coordinator.CompleteActivation(UwpStartupTimeout);

		_events.Append(LiveDebugEventKind.SessionNotice, $"Activated {appUserModelId} from birth as pid {pid}.");
		return pid;
	}

	/// <summary>
	/// The pre-#5 fallback: put the package into debug mode, activate it, and attach a beat later, by
	/// which time the very first OnLaunched has already run.
	/// </summary>
	private int ActivateUwpPostStartup(CorDebugSession session, string appUserModelId, string packageFullName)
	{
		Uwp.EnableDebugging(packageFullName);
		lock (_gate)
		{
			_uwpPackageFullName = packageFullName;
		}

		_events.Append(LiveDebugEventKind.SessionNotice, $"Enabled debug mode on {packageFullName}.");

		var pid = Uwp.ActivateApplication(appUserModelId);
		_events.Append(LiveDebugEventKind.SessionNotice, $"Activated {appUserModelId} as pid {pid}.");

		session.Attach(pid, UwpRuntimeReadyTimeout);
		return pid;
	}

	/// <summary>Turns off a package's debug mode, if one was enabled, restoring its normal lifecycle.</summary>
	private void DisableUwpDebugging()
	{
		string? packageFullName;
		lock (_gate)
		{
			packageFullName = _uwpPackageFullName;
			_uwpPackageFullName = null;
		}

		if (packageFullName is null) return;

		try
		{
			Uwp.DisableDebugging(packageFullName);
			logger.LogInformation("Disabled debug mode on {Package}.", packageFullName);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Disabling debug mode on {Package} failed.", packageFullName);
		}
	}

	private LiveAppSessionState EffectiveState()
	{
		if (_state == LiveAppSessionState.Ready && _session?.HasExited == true) return LiveAppSessionState.Ended;
		return _state;
	}

	private CorDebugSession RequireSession()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return session ?? throw new InvalidOperationException("This session is not attached to a target.");
	}

	private void Fault(string detail)
	{
		lock (_gate)
		{
			_state = LiveAppSessionState.Faulted;
			_detail = detail;
		}

		logger.LogWarning("Live-app session faulted: {Detail}", detail);
	}
}
