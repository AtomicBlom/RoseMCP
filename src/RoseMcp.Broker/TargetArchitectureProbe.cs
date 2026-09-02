using System.Reflection.PortableExecutable;
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

	/// <summary>
	/// The architecture an executable will run as, from its PE header, so a launch host is spawned to
	/// match. A native apphost names its architecture directly; an AnyCPU IL assembly runs as the host's
	/// own architecture, which is reported as Unknown so the launcher falls back to the broker's.
	/// </summary>
	public static TargetArchitecture ForExecutable(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			using var pe = new PEReader(stream);
			var headers = pe.PEHeaders;

			var cor = headers.CorHeader;
			var isAnyCpuIl = cor is not null
				&& (cor.Flags & CorFlags.ILOnly) != 0
				&& (cor.Flags & CorFlags.Requires32Bit) == 0;
			if (isAnyCpuIl) return TargetArchitecture.Unknown;

			return headers.CoffHeader.Machine switch
			{
				Machine.I386 => TargetArchitecture.X86,
				Machine.Amd64 => TargetArchitecture.X64,
				Machine.Arm64 => TargetArchitecture.Arm64,
				_ => TargetArchitecture.Unknown,
			};
		}
		catch (Exception)
		{
			return TargetArchitecture.Unknown;
		}
	}

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
