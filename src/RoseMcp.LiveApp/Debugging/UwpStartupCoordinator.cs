using System.IO.Pipes;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Coordinates the from-birth activation of a UWP app (issue #5). The system creates the app suspended
/// and launches this host again in resume-stub mode; the two ends meet on a named pipe. Activation
/// itself blocks until the app is resumed, so it is run on a background thread while this side waits for
/// the stub to report the app's ids, arms the debugger's runtime-startup notification, and only then
/// tells the stub to resume -- the ordering that lets the debugger see the runtime's first breath.
/// </summary>
internal sealed class UwpStartupCoordinator : IDisposable
{
	// The debugger command line the system stores has a limit around 256 characters; stay clear of it.
	private const int MaxDebuggerCommandLine = 255;

	private readonly string _pipeName;
	private readonly NamedPipeServerStream _pipe;
	private StreamReader? _reader;
	private StreamWriter? _writer;
	private Task<int>? _activation;

	public UwpStartupCoordinator()
	{
		_pipeName = $"rose-uwp.{Environment.ProcessId}.{Random.Shared.Next():x8}";
		_pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
	}

	/// <summary>
	/// The debugger command line that relaunches this host as the resume stub, or null when it would
	/// exceed the system's length limit -- in which case the caller falls back to a post-startup attach.
	/// </summary>
	public string? TryBuildStubCommandLine()
	{
		var hostExecutable = Environment.ProcessPath;
		if (string.IsNullOrEmpty(hostExecutable)) return null;

		var command = $"\"{hostExecutable}\" {UwpResumeStub.ModeFlag} --pipe {_pipeName}";
		return command.Length <= MaxDebuggerCommandLine ? command : null;
	}

	/// <summary>Begins activation on a background thread; it does not return until the app is resumed.</summary>
	public void BeginActivation(string appUserModelId)
		=> _activation = Task.Run(() => Uwp.ActivateApplication(appUserModelId));

	/// <summary>Waits for the resume stub to connect and report the app's process and main-thread ids.</summary>
	public (int Pid, int Tid) WaitForStub(TimeSpan timeout)
	{
		if (!_pipe.WaitForConnectionAsync().Wait(timeout))
		{
			throw new TimeoutException("The UWP resume stub did not connect; the app may not have activated under the debugger.");
		}

		_reader = new StreamReader(_pipe, leaveOpen: true);
		_writer = new StreamWriter(_pipe) { AutoFlush = true };

		var line = ReadLine(_reader, timeout)
			?? throw new TimeoutException("The UWP resume stub connected but reported no process id.");

		var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length < 2 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var tid))
		{
			throw new InvalidOperationException($"The UWP resume stub reported malformed ids: '{line}'.");
		}

		return (pid, tid);
	}

	/// <summary>
	/// Tells the stub to resume the app's main thread. Handed to the session as its resume action, so it
	/// runs at exactly the point the debugger has armed its startup notification and is ready to attach.
	/// </summary>
	public void Resume() => _writer?.WriteLine("resume");

	/// <summary>Waits for activation to return once the app has been resumed, and yields the pid.</summary>
	public int CompleteActivation(TimeSpan timeout)
	{
		if (_activation is null) throw new InvalidOperationException("Activation was not begun.");

		try
		{
			if (!_activation.Wait(timeout))
			{
				throw new TimeoutException("ActivateApplication did not return after the app was resumed.");
			}
		}
		catch (AggregateException aggregate) when (aggregate.InnerException is not null)
		{
			throw aggregate.InnerException;
		}

		return _activation.Result;
	}

	private static string? ReadLine(StreamReader reader, TimeSpan timeout)
	{
		var task = reader.ReadLineAsync();
		return task.Wait(timeout) ? task.Result : null;
	}

	public void Dispose()
	{
		try
		{
			_writer?.Dispose();
			_reader?.Dispose();
			_pipe.Dispose();
		}
		catch (Exception)
		{
			// Teardown of a coordination pipe is best-effort; the app's own lifecycle is not tied to it.
		}
	}
}
