using System.Globalization;
using System.Runtime.InteropServices;
using ClrDebug;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// The two things dbgshim does between a version string and an ICorDebug, done by hand: open the
/// debuggee to see where its runtime lives, then ask the mscordbi beside that runtime for a
/// debugger object. dbgshim folds every failure on that path into CORDBG_E_DEBUG_COMPONENT_MISSING,
/// which is why this exists -- doing the steps here says which one failed, and with what.
/// </summary>
internal static partial class RuntimeDiscovery
{
	private const uint ProcessAllAccess = 0x001FFFFF;
	private const uint ProcessQueryInformation = 0x0400;
	private const uint ProcessVmRead = 0x0010;
	private const int MaxPathChars = 4096;

	/// <summary>dbgshim's version string is "dbiVersion;pid;hmodTargetCLR", each in hex.</summary>
	public static (int DbiVersion, int Pid, IntPtr Hmod) ParseVersionString(string version)
	{
		var parts = version.Split(';');
		if (parts.Length != 3)
		{
			throw new FormatException($"Unexpected version string '{version}'.");
		}

		return (
			int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			(IntPtr)long.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
	}

	/// <summary>What dbgshim's GetDbiFilenameNextToRuntime would see, one line per attempt.</summary>
	public static IReadOnlyList<string> Probe(int pid, IntPtr hmod)
	{
		var lines = new List<string>();
		var attempts = new[]
		{
			("PROCESS_ALL_ACCESS", ProcessAllAccess),
			("QUERY_INFORMATION|VM_READ", ProcessQueryInformation | ProcessVmRead),
		};

		foreach (var (name, access) in attempts)
		{
			var handle = OpenProcess(access, false, pid);
			if (handle == IntPtr.Zero)
			{
				lines.Add($"OpenProcess({name}) failed, Win32 error {Marshal.GetLastPInvokeError()}");
				continue;
			}

			try
			{
				var path = ModulePath(handle, hmod, out var error);
				lines.Add($"OpenProcess({name}) ok; GetModuleFileNameEx -> {path ?? $"failed, Win32 error {error}"}");
			}
			finally
			{
				CloseHandle(handle);
			}
		}

		return lines;
	}

	/// <summary>
	/// Skip dbgshim's discovery and call the runtime's own mscordbi, which is what dbgshim would
	/// have done next. The returned object is handed to ClrDebug's wrapper through the same
	/// ComWrappers instance ClrDebug marshals with, so the rest of the session is unchanged.
	/// </summary>
	public static CorDebug CreateCorDebug(string runtimePath, int pid, IntPtr hmod, CorDebugInterfaceVersion debuggerVersion)
	{
		var dbiPath = Path.Combine(Path.GetDirectoryName(runtimePath)!, "mscordbi.dll");
		if (!File.Exists(dbiPath))
		{
			throw new FileNotFoundException("No mscordbi beside the debuggee's runtime.", dbiPath);
		}

		var dbi = NativeLibrary.Load(dbiPath);
		var export = NativeLibrary.GetExport(dbi, "CoreCLRCreateCordbObject3");
		var create = Marshal.GetDelegateForFunctionPointer<CoreCLRCreateCordbObject3>(export);

		var hr = create((int)debuggerVersion, (uint)pid, IntPtr.Zero, IntPtr.Zero, hmod, out var unknown);
		if (hr < 0)
		{
			throw new InvalidOperationException($"CoreCLRCreateCordbObject3 in {dbiPath} returned 0x{hr:x8}.");
		}

		try
		{
			// ClrDebug wraps the raw pointer with the same ComWrappers instance it marshals with,
			// so the rest of the session cannot tell this object came from a different door.
			var wrapped = Extensions.GetObjectForIUnknown<ICorDebug>(unknown);
			return new CorDebug(wrapped);
		}
		finally
		{
			Marshal.Release(unknown);
		}
	}

	private static string? ModulePath(IntPtr process, IntPtr module, out int error)
	{
		var buffer = Marshal.AllocHGlobal(MaxPathChars * sizeof(char));
		try
		{
			var length = GetModuleFileNameEx(process, module, buffer, MaxPathChars);
			error = length == 0 ? Marshal.GetLastPInvokeError() : 0;
			return length == 0 ? null : Marshal.PtrToStringUni(buffer, (int)length);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int CoreCLRCreateCordbObject3(int iDebuggerVersion, uint pid, IntPtr lpApplicationGroupId, IntPtr dacModulePath, IntPtr hmodTargetCLR, out IntPtr ppCordb);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(IntPtr handle);

	[LibraryImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", SetLastError = true)]
	private static partial uint GetModuleFileNameEx(IntPtr process, IntPtr module, IntPtr buffer, uint size);
}
