using System.Runtime.InteropServices;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Works out the architecture a running process is executing as. The live-app host must match it,
/// because the host loads the target's own debugging and diagnostics DLLs into itself. On
/// Windows-on-ARM a classic UWP process runs x64 under emulation while the machine is ARM64, so this
/// asks the process, not the machine.
/// </summary>
public static class TargetArchitectureProbe
{
	private const uint ProcessQueryLimitedInformation = 0x1000;

	// IMAGE_FILE_MACHINE_* values IsWow64Process2 reports.
	private const ushort MachineUnknown = 0x0000;
	private const ushort MachineI386 = 0x014c;
	private const ushort MachineArmNt = 0x01c4;
	private const ushort MachineAmd64 = 0x8664;
	private const ushort MachineArm64 = 0xAA64;

	public static TargetArchitecture ForProcess(int processId)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return TargetArchitecture.Unknown;

		var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (handle == IntPtr.Zero) return TargetArchitecture.Unknown;

		try
		{
			if (!IsWow64Process2(handle, out var processMachine, out var nativeMachine))
			{
				return TargetArchitecture.Unknown;
			}

			// A non-UNKNOWN processMachine means the process is emulated, and names the emulated
			// architecture; UNKNOWN means it runs natively, so the machine's own architecture is it.
			var machine = processMachine == MachineUnknown ? nativeMachine : processMachine;
			return machine switch
			{
				MachineI386 => TargetArchitecture.X86,
				MachineArmNt => TargetArchitecture.Unknown,
				MachineAmd64 => TargetArchitecture.X64,
				MachineArm64 => TargetArchitecture.Arm64,
				_ => TargetArchitecture.Unknown,
			};
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
