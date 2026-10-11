using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoseMcp.Broker;

/// <summary>
/// Which packaged app a running process is, read from its package identity. One reader for the running-app list and
/// for the session manager's one-debugger rule, which has to know that a process attached to by its id is the same
/// app a launch names by its application user-model id.
/// </summary>
public static class PackagedProcess
{
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const int ErrorInsufficientBuffer = 122;

	/// <summary>
	/// The application user-model id the process runs as, or null for a process with no package identity, one that
	/// cannot be opened, and every process off Windows.
	/// </summary>
	public static string? AppUserModelIdOf(int processId)
	{
		if (!OperatingSystem.IsWindows()) return null;

		var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (handle == IntPtr.Zero) return null;

		try
		{
			return AppUserModelIdOf(handle);
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	/// <summary>The application user-model id of a process already opened for at least limited query, or null where it has none.</summary>
	[SupportedOSPlatform("windows")]
	public static string? AppUserModelIdOf(IntPtr process)
	{
		uint length = 0;
		var result = GetApplicationUserModelId(process, ref length, null);
		if (result != ErrorInsufficientBuffer || length == 0) return null;

		var buffer = new char[length];
		result = GetApplicationUserModelId(process, ref length, buffer);

		return result == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)) : null;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, [Out] char[]? applicationUserModelId);
}
