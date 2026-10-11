using System.Globalization;
using System.Runtime.InteropServices;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The machine's physical memory and how much of it is free: the system's own figures on Windows and on Linux, and
/// elsewhere the total the runtime sees with nothing said about what is free. Never throws; a machine it cannot
/// read is null.
/// </summary>
public static class MachineMemoryProbe
{
	/// <summary>What the machine has, and what is free, as of now.</summary>
	public static MachineMemory? Read()
	{
		try
		{
			if (OperatingSystem.IsWindows()) return ReadWindows();
			if (OperatingSystem.IsLinux() && ReadLinux() is { } linux) return linux;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException or ExternalException)
		{
			// Fall through to what the runtime knows.
		}

		var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
		return total > 0 ? new MachineMemory { TotalBytes = total } : null;
	}

	/// <summary>
	/// Reads <c>/proc/meminfo</c>, whose MemAvailable counts the cache the kernel can give back, which MemFree does
	/// not; a kernel too old to have it says nothing about what is free.
	/// </summary>
	public static MachineMemory? ParseMeminfo(IEnumerable<string> lines)
	{
		long? total = null;
		long? available = null;

		foreach (var line in lines)
		{
			if (Field(line, "MemTotal:") is { } totalKb) total = totalKb * 1024;
			else if (Field(line, "MemAvailable:") is { } availableKb) available = availableKb * 1024;
		}

		return total is { } bytes ? new MachineMemory { TotalBytes = bytes, AvailableBytes = available } : null;
	}

	private static long? Field(string line, string name)
	{
		if (!line.StartsWith(name, StringComparison.Ordinal)) return null;

		var value = line[name.Length..].Trim();
		var space = value.IndexOf(' ', StringComparison.Ordinal);
		if (space > 0) value = value[..space];

		return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb : null;
	}

	private static MachineMemory? ReadLinux()
	{
		const string Meminfo = "/proc/meminfo";
		return File.Exists(Meminfo) ? ParseMeminfo(File.ReadLines(Meminfo)) : null;
	}

	private static MachineMemory? ReadWindows()
	{
		var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
		if (!GlobalMemoryStatusEx(ref status)) return null;

		return new MachineMemory
		{
			TotalBytes = (long)status.TotalPhysical,
			AvailableBytes = (long)status.AvailablePhysical,
		};
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	/// <summary>MEMORYSTATUSEX, field for field.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatusEx
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhysical;
		public ulong AvailablePhysical;
		public ulong TotalPageFile;
		public ulong AvailablePageFile;
		public ulong TotalVirtual;
		public ulong AvailableVirtual;
		public ulong AvailableExtendedVirtual;
	}
}
