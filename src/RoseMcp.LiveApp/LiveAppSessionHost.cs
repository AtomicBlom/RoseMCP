using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;

namespace RoseMcp.LiveApp;

/// <summary>
/// Owns the one target this host was launched for. For an attach it establishes a real ICorDebug
/// session over the target (issue #4, attach path) and captures its debug events into a buffer the
/// broker reads (issue #8). The XAML provider and the launch paths come in later issues; this is the
/// shell the broker supervises and everything else plugs into.
/// </summary>
public sealed class LiveAppSessionHost(LiveAppOptions options, ILogger<LiveAppSessionHost> logger) : IHostedService
{
	private readonly Lock _gate = new();
	private readonly DebugEventBuffer _events = new();
	private LiveAppSessionState _state = LiveAppSessionState.Starting;
	private int? _targetProcessId;
	private string? _detail;
	private CorDebugSession? _session;

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

			// LaunchUwp is a later slice of issue #4; for now the shell reports honestly.
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
