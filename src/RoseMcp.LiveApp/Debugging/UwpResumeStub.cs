using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// The from-birth hop for a packaged app (issue #5). A plain executable is launched suspended by
/// dbgshim itself; a UWP app can only be started by the shell's activation, so the system is asked to
/// create it suspended and to launch THIS executable, in stub mode, as the app's debugger. The system
/// appends <c>-p &lt;pid&gt; -tid &lt;tid&gt;</c> to the registered command line; the stub reports those
/// ids to the waiting host over a named pipe, waits until the host has armed its runtime-startup
/// notification, and only then resumes the app's main thread -- so the runtime loads under debug from
/// its first instruction. If the host never answers, the stub resumes the app anyway, so a missing or
/// crashed host degrades to a normal (post-startup) run rather than a process wedged forever.
/// </summary>
internal static class UwpResumeStub
{
	public const string ModeFlag = "--uwp-resume-stub";

	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(20);

	public static int Run(string[] args)
	{
		var pipeName = ValueAfter(args, "--pipe");
		var tid = IntAfter(args, "-tid");
		var pid = IntAfter(args, "-p");
		var resumed = false;
		try
		{
			if (pipeName is null || pid is null || tid is null)
			{
				// Nothing to coordinate with; let the app run so it is not left suspended.
				resumed = ResumeMainThread(tid);
				return 2;
			}

			resumed = Coordinate(pipeName, pid.Value, tid.Value);
			return resumed ? 0 : 3;
		}
		catch (Exception)
		{
			return 1;
		}
		finally
		{
			// Fail-safe: whatever went wrong above, never leave the app's main thread suspended.
			if (!resumed) ResumeMainThread(tid);
		}
	}

	private static bool Coordinate(string pipeName, int pid, int tid)
	{
		using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
		}
		catch (Exception)
		{
			return false; // Host not listening; the finally will resume the app.
		}

		using var reader = new StreamReader(pipe);
		using var writer = new StreamWriter(pipe) { AutoFlush = true };

		// Tell the host which process and thread the system handed us.
		writer.WriteLine($"{pid} {tid}");

		// Wait for the host to arm its runtime-startup notification before we let the runtime load.
		var go = ReadLineWithTimeout(reader, ResumeTimeout);
		if (!string.Equals(go?.Trim(), "resume", StringComparison.Ordinal)) return false;

		var resumed = ResumeMainThread(tid);
		writer.WriteLine(resumed ? "resumed" : "resume-failed");
		return resumed;
	}

	private static string? ReadLineWithTimeout(StreamReader reader, TimeSpan timeout)
	{
		var task = reader.ReadLineAsync();
		return task.Wait(timeout) ? task.Result : null;
	}

	/// <summary>Resumes the app's main thread, undoing the birth suspension. Idempotent enough: the thread
	/// was created with one suspend, so a single resume runs it.</summary>
	private static bool ResumeMainThread(int? tid)
	{
		if (tid is not { } threadId) return false;

		var handle = OpenThread(ThreadSuspendResume, bInheritHandle: false, (uint)threadId);
		if (handle == IntPtr.Zero) return false;

		try
		{
			return ResumeThread(handle) != unchecked((uint)-1);
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private static string? ValueAfter(string[] args, string flag)
	{
		for (var i = 0; i < args.Length - 1; i++)
		{
			if (string.Equals(args[i], flag, StringComparison.Ordinal)) return args[i + 1];
		}

		return null;
	}

	private static int? IntAfter(string[] args, string flag)
		=> ValueAfter(args, flag) is { } value && int.TryParse(value, out var parsed) ? parsed : null;

	private const int ThreadSuspendResume = 0x0002;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenThread(int desiredAccess, bool bInheritHandle, uint threadId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint ResumeThread(IntPtr threadHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
