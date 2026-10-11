using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The running .NET processes of this user, read from Windows: the top-level windows on the desktop and the processes
/// they belong to, each process's loaded modules, and the diagnostics pipes .NET opens.
/// <para>
/// A packaged app's window belongs to the frame host rather than the app: its title is on an
/// <c>ApplicationFrameWindow</c> the frame host owns, and the app's own <c>CoreWindow</c> is a child of it. So a frame
/// window is credited to the process of the core window inside it, which is the app a person sees.
/// </para>
/// <para>
/// A process this user cannot open for reading -- another user's, or an elevated one -- is left out, as attaching to
/// it would be refused anyway. Off Windows there is nothing to read and the list is empty.
/// </para>
/// </summary>
public sealed class WindowsRunningApps : IRunningApps
{
	private const uint ProcessQueryInformation = 0x0400;
	private const uint ProcessVmRead = 0x0010;
	private const uint ListModulesAll = 0x03;
	private const uint GwOwner = 4;
	private const int GwlExStyle = -20;
	private const long WsExToolWindow = 0x00000080;

	/// <summary>The lowest process id a user's process has; below it are the idle and system processes.</summary>
	private const int LowestUserProcessId = 8;

	/// <inheritdoc />
	public IReadOnlyList<RunningApp> Read(bool every)
	{
		if (!OperatingSystem.IsWindows()) return [];

		try
		{
			return ReadWindows(every);
		}
		catch (Exception)
		{
			// Reading the desktop is best effort: a list that could not be read is an empty one, and the page says so.
			return [];
		}
	}

	[SupportedOSPlatform("windows")]
	private static IReadOnlyList<RunningApp> ReadWindows(bool every)
	{
		var windows = TitlesByProcess();
		var piped = DiagnosticPipeProcesses();

		var candidates = new HashSet<int>(windows.Keys);
		if (every) candidates.UnionWith(piped);

		var apps = new List<RunningApp>();

		foreach (var processId in candidates)
		{
			var isOurs = processId == Environment.ProcessId || processId < LowestUserProcessId;
			if (isOurs) continue;

			try
			{
				if (Describe(processId, windows, piped) is { } app) apps.Add(app);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// One process that cannot be read -- exited between the listing and the read, say -- is left out alone.
			}
		}

		return RunningApps.Ordered(apps);
	}

	/// <summary>One process as a row, or null where it is not .NET, is part of Rose, or cannot be read.</summary>
	[SupportedOSPlatform("windows")]
	private static RunningApp? Describe(int processId, IReadOnlyDictionary<int, string> windows, IReadOnlySet<int> piped)
	{
		var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
		if (handle == IntPtr.Zero) return null;

		try
		{
			var modules = ModuleNames(handle);
			var isDotNet = RunningApps.RunsDotNet(modules) || piped.Contains(processId);
			if (!isDotNet) return null;

			var path = ImagePath(handle);
			var name = path is null ? $"pid {processId}" : Path.GetFileNameWithoutExtension(path);
			if (RunningApps.IsRose(name)) return null;

			return new RunningApp
			{
				ProcessId = processId,
				Name = name,
				WindowTitle = windows.GetValueOrDefault(processId),
				ExecutablePath = path,
				XamlStack = XamlStackModules.Identify(modules).Stack,
				AppUserModelId = PackagedProcess.AppUserModelIdOf(handle),
				Architecture = TargetArchitectureProbe.ForProcess(processId),
				HasWindow = windows.ContainsKey(processId),
			};
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	/// <summary>
	/// Each process with a visible, unowned, titled top-level window that is not a tool window, to that window's
	/// title; a packaged app's frame window credited to the app inside it.
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static Dictionary<int, string> TitlesByProcess()
	{
		var titles = new Dictionary<int, string>();

		EnumWindowsProc visit = (window, _) =>
		{
			Consider(window, titles);
			return true;
		};

		EnumWindows(visit, IntPtr.Zero);
		GC.KeepAlive(visit);

		return titles;
	}

	[SupportedOSPlatform("windows")]
	private static void Consider(IntPtr window, Dictionary<int, string> titles)
	{
		if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero) return;

		var isToolWindow = (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0;
		if (isToolWindow) return;

		var title = Text(window);
		if (string.IsNullOrWhiteSpace(title)) return;

		// A packaged app's title is on the frame host's window, and the app is the core window inside it.
		var core = ClassName(window) == "ApplicationFrameWindow"
			? FindWindowEx(window, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null)
			: IntPtr.Zero;
		var owner = core != IntPtr.Zero ? core : window;

		GetWindowThreadProcessId(owner, out var processId);
		if (processId > 0) titles.TryAdd((int)processId, title);
	}

	/// <summary>The processes with a .NET diagnostics pipe open, which every .NET process of this user opens by default.</summary>
	[SupportedOSPlatform("windows")]
	private static HashSet<int> DiagnosticPipeProcesses()
	{
		var processes = new HashSet<int>();

		try
		{
			foreach (var pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
			{
				if (RunningApps.DiagnosticPipeProcessId(pipe) is { } processId) processes.Add(processId);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			// The pipe namespace could not be listed; the modules still say which processes run .NET.
		}

		return processes;
	}

	[SupportedOSPlatform("windows")]
	private static List<string> ModuleNames(IntPtr process)
	{
		var modules = new IntPtr[256];

		while (true)
		{
			var size = (uint)(modules.Length * IntPtr.Size);
			if (!EnumProcessModulesEx(process, modules, size, out var needed, ListModulesAll)) return [];

			var count = (int)(needed / IntPtr.Size);
			if (count <= modules.Length)
			{
				var names = new List<string>(count);
				var buffer = new char[260];

				for (var index = 0; index < count; index++)
				{
					var length = GetModuleBaseNameW(process, modules[index], buffer, (uint)buffer.Length);
					if (length > 0) names.Add(new string(buffer, 0, (int)length));
				}

				return names;
			}

			modules = new IntPtr[count + 16];
		}
	}

	[SupportedOSPlatform("windows")]
	private static string? ImagePath(IntPtr process)
	{
		var buffer = new char[1024];
		var length = buffer.Length;

		return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
	}

	[SupportedOSPlatform("windows")]
	private static string Text(IntPtr window)
	{
		var length = GetWindowTextLengthW(window);
		if (length <= 0) return string.Empty;

		var buffer = new char[length + 1];
		var read = GetWindowTextW(window, buffer, buffer.Length);

		return read > 0 ? new string(buffer, 0, read) : string.Empty;
	}

	[SupportedOSPlatform("windows")]
	private static string ClassName(IntPtr window)
	{
		var buffer = new char[256];
		var read = GetClassNameW(window, buffer, buffer.Length);

		return read > 0 ? new string(buffer, 0, read) : string.Empty;
	}

	/// <summary>Called by <c>EnumWindows</c> for each top-level window; true carries on.</summary>
	private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr window, uint command);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextLengthW(IntPtr window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(IntPtr window, [Out] char[] text, int maxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(IntPtr window, [Out] char[] className, int maxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("psapi.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint size, out uint needed, uint filter);

	[DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetModuleBaseNameW(IntPtr process, IntPtr module, [Out] char[] baseName, uint size);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref int size);
}
