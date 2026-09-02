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

			// Launching is a later slice of issue #4; for now the shell reports honestly.
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

	private LiveAppSessionState EffectiveState()
	{
		if (_state == LiveAppSessionState.Ready && _session?.HasExited == true) return LiveAppSessionState.Ended;
		return _state;
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
