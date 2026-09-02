using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp;

/// <summary>
/// Owns the one target this host was launched for. In this foundation build it *establishes* the
/// target -- for an attach, that means confirming the process is alive -- and reports state. The
/// actual ICorDebug attach and the XAML provider come in later issues; this is the shell the broker
/// supervises and everything else plugs into.
/// </summary>
public sealed class LiveAppSessionHost(LiveAppOptions options, ILogger<LiveAppSessionHost> logger) : IHostedService
{
	private readonly Lock _gate = new();
	private LiveAppSessionState _state = LiveAppSessionState.Starting;
	private int? _targetProcessId;
	private string? _detail;

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
				State = _state,
				TargetProcessId = _targetProcessId,
				Detail = _detail,
			};
		}
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Establish();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			_state = LiveAppSessionState.Ended;
		}

		return Task.CompletedTask;
	}

	private void Establish()
	{
		switch (options.Target.Kind)
		{
			case LiveAppTargetKind.AttachProcess:
				EstablishAttach(options.Target.ProcessId);
				break;

			// Launching is issue #4; for now the shell reports honestly rather than pretending.
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
			using var process = Process.GetProcessById(pid);
			if (process.HasExited)
			{
				Fault($"Process {pid} has already exited.");
				return;
			}

			lock (_gate)
			{
				_targetProcessId = pid;
				_state = LiveAppSessionState.Ready;
				_detail = null;
			}

			logger.LogInformation("Live-app session established against pid {Pid} as {Architecture}.", pid, Architecture);
		}
		catch (ArgumentException)
		{
			Fault($"No process with id {pid} is running.");
		}
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
