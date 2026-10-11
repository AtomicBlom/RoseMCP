using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RoseMcp.Server;

/// <summary>
/// The process a stdio relay serves: the one that started it, past any shell in between.
/// <para>
/// A client starts its stdio servers itself, so the relay's parent is the client -- unless the client was
/// configured to start it through a shell, as <c>cmd /c</c> is often written into a Windows configuration,
/// in which case the client is the shell's parent. Only shells are walked past, and only a few levels, so
/// whatever is found is something that chose to start the relay.
/// </para>
/// <para>
/// A process id outlives its process, and Windows hands it to the next process it starts, so a parent that
/// started after its child is not the parent at all and the walk stops there. Anything that cannot be read --
/// another user's process, a platform with neither <c>/proc</c> nor <c>ntdll</c> -- ends the walk where it is.
/// </para>
/// </summary>
public static class ClientProcess
{
	private const int MaxShells = 4;

	private const uint ProcessQueryLimitedInformation = 0x1000;

	private const int ProcessBasicInformationClass = 0;

	/// <summary>The programs a client might start the relay through rather than directly.</summary>
	private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
	{
		"cmd", "powershell", "pwsh", "conhost", "sh", "bash", "dash", "zsh", "env",
	};

	/// <summary>The process that started this one, past any shell, with its name; null where it cannot be found.</summary>
	public static (int Id, string? Name)? Find()
	{
		using var self = Process.GetCurrentProcess();

		var child = self.Id;
		var childStarted = StartedUtc(self);
		(int Id, string? Name)? found = null;

		for (var level = 0; level <= MaxShells; level++)
		{
			if (ParentOf(child) is not { } parentId) break;

			using var parent = Open(parentId);
			if (parent is null) break;

			var parentStarted = StartedUtc(parent);
			var reused = parentStarted is { } born && childStarted is { } childBorn && born > childBorn;
			if (reused) break;

			var name = NameOf(parent);
			found = (parentId, name);

			if (name is null || !IsShell(name)) break;

			child = parentId;
			childStarted = parentStarted;
		}

		return found;
	}

	/// <summary>Whether a process of this name is a shell a client might start the relay through.</summary>
	public static bool IsShell(string processName) => Shells.Contains(processName);

	/// <summary>The id of the process that started <paramref name="processId"/>, where the platform says.</summary>
	public static int? ParentOf(int processId)
	{
		try
		{
			if (OperatingSystem.IsWindows()) return WindowsParentOf(processId);
			if (OperatingSystem.IsLinux()) return LinuxParentOf(processId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}

		return null;
	}

	/// <summary>The parent id in <c>/proc/&lt;id&gt;/stat</c>: the second field after the command, which is in parentheses and may contain anything.</summary>
	private static int? LinuxParentOf(int processId)
	{
		var stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
		var afterCommand = stat.LastIndexOf(')');
		if (afterCommand < 0) return null;

		var fields = stat[(afterCommand + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length < 2) return null;

		var parent = int.Parse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture);

		return parent > 0 ? parent : null;
	}

	private static int? WindowsParentOf(int processId)
	{
		var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
		if (handle == IntPtr.Zero) return null;

		try
		{
			var status = NtQueryInformationProcess(
				handle, ProcessBasicInformationClass, out var information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
			if (status != 0) return null;

			var parent = (int)information.InheritedFromUniqueProcessId;

			return parent > 0 ? parent : null;
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private static Process? Open(int processId)
	{
		try
		{
			return Process.GetProcessById(processId);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	private static string? NameOf(Process process)
	{
		try
		{
			return process.ProcessName;
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			return null;
		}
	}

	private static DateTime? StartedUtc(Process process)
	{
		try
		{
			return process.StartTime.ToUniversalTime();
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessBasicInformation
	{
		public IntPtr ExitStatus;
		public IntPtr PebBaseAddress;
		public IntPtr AffinityMask;
		public IntPtr BasePriority;
		public IntPtr UniqueProcessId;
		public IntPtr InheritedFromUniqueProcessId;
	}

	[DllImport("ntdll.dll")]
	private static extern int NtQueryInformationProcess(
		IntPtr process, int informationClass, out ProcessBasicInformation information, int length, out int returnLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
