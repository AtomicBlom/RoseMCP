using System.Diagnostics;
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

	/// <summary>
	/// The architecture a packaged app will run as, read from its package identity rather than
	/// assumed, so the host is chosen before anything is activated.
	/// <para>
	/// Asking is not a refinement here, it is the difference between working and not. Classic UWP is
	/// debuggable only as <c>Debug|x64</c> -- every other configuration forces .NET Native, which has
	/// no CoreCLR for ICorDebug to attach to -- so assuming x64 was true of every UWP target that
	/// could be debugged at all. A UWP app on modern .NET is CoreCLR in x86, x64 and ARM64 alike, and
	/// the standard project template leads with x86, so the assumption now names the wrong host for
	/// an ordinary target. What made that expensive is the order: activation happens first, so the
	/// app is running by the time the mismatched host fails to find a runtime in it, and the error
	/// blames the target rather than the choice made before it started.
	/// </para>
	/// <para>
	/// The architecture is the third field of the package full name
	/// (<c>Name_Version_Arch_ResourceId_PublisherHash</c>), which is the identity Windows itself
	/// registered rather than anything read out of a file that may have been rebuilt since. A family
	/// with several registered packages prefers the machine's own architecture, because that is the
	/// one Windows activates; <c>neutral</c> names no architecture and is left Unknown, which falls
	/// back to the broker's own.
	/// </para>
	/// </summary>
	/// <param name="appUserModelId">The AUMID, as <c>PackageFamilyName!AppId</c>.</param>
	public static TargetArchitecture ForPackage(string appUserModelId)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return TargetArchitecture.Unknown;

		var separator = appUserModelId.IndexOf('!');
		var family = separator < 0 ? appUserModelId : appUserModelId[..separator];
		if (family.Length == 0) return TargetArchitecture.Unknown;

		var native = RuntimeInformation.OSArchitecture switch
		{
			Architecture.Arm64 => TargetArchitecture.Arm64,
			Architecture.X86 => TargetArchitecture.X86,
			_ => TargetArchitecture.X64,
		};

		var fallback = TargetArchitecture.Unknown;
		foreach (var fullName in PackageFullNames(family))
		{
			var architecture = ArchitectureFromFullName(fullName);
			if (architecture == TargetArchitecture.Unknown) continue;
			if (architecture == native) return architecture;
			if (fallback == TargetArchitecture.Unknown) fallback = architecture;
		}

		return fallback;
	}

	/// <summary>
	/// The architecture named by a package full name, or Unknown for a name that does not carry one.
	/// A package name cannot contain an underscore, so the five fields split cleanly.
	/// </summary>
	public static TargetArchitecture ArchitectureFromFullName(string packageFullName)
	{
		var parts = packageFullName.Split('_');
		if (parts.Length != 5) return TargetArchitecture.Unknown;

		return parts[2].ToLowerInvariant() switch
		{
			"x86" => TargetArchitecture.X86,
			"x64" => TargetArchitecture.X64,
			"arm64" => TargetArchitecture.Arm64,

			// "arm" is 32-bit ARM and "neutral" names no architecture at all; neither has a host here.
			_ => TargetArchitecture.Unknown,
		};
	}

	/// <summary>
	/// The full names of every registered package in a family, or empty when there are none and when
	/// they cannot be read -- this runs before a session starts and must not become the thing that
	/// stops one.
	/// </summary>
	private static IReadOnlyList<string> PackageFullNames(string familyName)
	{
		uint count = 0;
		uint bufferLength = 0;

		var result = FindPackagesByPackageFamily(
			familyName, PackageFilterHead, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero, IntPtr.Zero);
		if (result != ErrorInsufficientBuffer || count == 0) return [];

		var names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
		var buffer = Marshal.AllocHGlobal((int)bufferLength * sizeof(char));

		try
		{
			result = FindPackagesByPackageFamily(
				familyName, PackageFilterHead, ref count, names, ref bufferLength, buffer, IntPtr.Zero);
			if (result != 0) return [];

			var fullNames = new List<string>((int)count);
			for (var index = 0; index < count; index++)
			{
				var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
				if (name is not null) fullNames.Add(name);
			}

			return fullNames;
		}
		catch (Exception)
		{
			return [];
		}
		finally
		{
			Marshal.FreeHGlobal(names);
			Marshal.FreeHGlobal(buffer);
		}
	}

	/// <summary>PACKAGE_FILTER_HEAD: the packages themselves, not their resource or optional bundles.</summary>
	private const uint PackageFilterHead = 0x00000010;

	private const int ErrorInsufficientBuffer = 122;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int FindPackagesByPackageFamily(
		string packageFamilyName,
		uint packageFilters,
		ref uint count,
		IntPtr packageFullNames,
		ref uint bufferLength,
		IntPtr buffer,
		IntPtr packageProperties);

	public static TargetArchitecture ForProcess(int processId)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return TargetArchitecture.Unknown;

		// The most reliable signal is the target's own executable image. IsWow64Process2 is not a
		// dependable discriminator for x64-on-ARM64: a genuinely-x64 process there can report
		// processMachine=UNKNOWN, which would misread as the native (ARM64) architecture and pick the
		// wrong host. Reading the image's PE machine says what it actually is.
		var fromImage = ArchitectureFromImage(processId);
		if (fromImage != TargetArchitecture.Unknown) return fromImage;

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

	/// <summary>The architecture of a running process, read from its main module's PE header.</summary>
	private static TargetArchitecture ArchitectureFromImage(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			var path = process.MainModule?.FileName;
			return path is null ? TargetArchitecture.Unknown : ForExecutable(path);
		}
		catch (Exception)
		{
			return TargetArchitecture.Unknown;
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
