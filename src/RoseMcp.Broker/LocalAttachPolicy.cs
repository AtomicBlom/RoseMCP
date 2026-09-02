using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace RoseMcp.Broker;

/// <summary>
/// The minimal gate that ships with the first attach (issue #15, first step). Attaching a debugger is
/// powerful, so the agent-facing surface only permits what the dogfood needs: a process that is local,
/// running, not a protected system process, and owned by the same user. Anything else is refused with
/// a plain reason rather than handed to ICorDebug.
/// <para>
/// The strongest enforcement is the operating system's own: <c>DebugActiveProcess</c> fails unless the
/// caller's token permits debugging the target, which normally means the same user. This adds a clear,
/// early refusal on top of that, and a documented policy for the fuller model to build on.
/// </para>
/// </summary>
public static class LocalAttachPolicy
{
	// pids 0 (System Idle) and 4 (System) are never legitimate managed-debug targets.
	private const int LowestUserProcessId = 8;

	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const uint TokenQuery = 0x0008;
	private const int TokenUserInformationClass = 1;

	/// <summary>Throws with a plain reason if <paramref name="processId"/> may not be attached to.</summary>
	public static void EnsureAttachable(int processId)
	{
		if (processId < LowestUserProcessId)
		{
			throw new ArgumentException($"pid {processId} is a system process and cannot be attached to.");
		}

		Process process;
		try
		{
			process = Process.GetProcessById(processId);
		}
		catch (ArgumentException)
		{
			throw new ArgumentException($"No local process with id {processId} is running.");
		}

		using (process)
		{
			var exited = false;
			try
			{
				exited = process.HasExited;
			}
			catch (Exception)
			{
				// Access to liveness was denied; the owner check or the OS will refuse if it must.
			}

			if (exited) throw new ArgumentException($"Process {processId} has already exited.");
		}

		if (OperatingSystem.IsWindows()) EnsureSameUser(processId);
	}

	[SupportedOSPlatform("windows")]
	private static void EnsureSameUser(int processId)
	{
		var owner = TryGetProcessOwner(processId);
		if (owner is null) return; // Could not determine; let DebugActiveProcess's own ACL decide.

		var self = WindowsIdentity.GetCurrent().User;
		if (self is not null && !owner.Equals(self))
		{
			throw new ArgumentException(
				$"pid {processId} runs as a different user; only same-user processes can be attached to.");
		}
	}

	[SupportedOSPlatform("windows")]
	private static SecurityIdentifier? TryGetProcessOwner(int processId)
	{
		var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (process == IntPtr.Zero) return null;

		try
		{
			if (!OpenProcessToken(process, TokenQuery, out var token)) return null;

			try
			{
				return ReadTokenUser(token);
			}
			finally
			{
				CloseHandle(token);
			}
		}
		finally
		{
			CloseHandle(process);
		}
	}

	[SupportedOSPlatform("windows")]
	private static SecurityIdentifier? ReadTokenUser(IntPtr token)
	{
		GetTokenInformation(token, TokenUserInformationClass, IntPtr.Zero, 0, out var needed);
		if (needed == 0) return null;

		var buffer = Marshal.AllocHGlobal((int)needed);
		try
		{
			if (!GetTokenInformation(token, TokenUserInformationClass, buffer, needed, out _)) return null;

			// TOKEN_USER begins with a SID_AND_ATTRIBUTES whose first field is the PSID.
			var sidPointer = Marshal.ReadIntPtr(buffer);
			return sidPointer == IntPtr.Zero ? null : new SecurityIdentifier(sidPointer);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetTokenInformation(IntPtr token, int tokenInformationClass, IntPtr buffer, uint length, out uint returnLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
