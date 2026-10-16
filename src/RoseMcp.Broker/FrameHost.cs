using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoseMcp.Broker;

/// <summary>
/// One app whose window <c>ApplicationFrameHost</c> draws the frame around: the app's own process, and the title the
/// frame shows.
/// </summary>
/// <param name="ProcessId">The app's own process, which owns the <c>CoreWindow</c> inside the frame.</param>
/// <param name="Title">The window's title, which is what a person or an agent found the frame host by.</param>
public sealed record FramedApp(int ProcessId, string Title);

/// <summary>
/// <c>ApplicationFrameHost</c>, the system process that draws the title bar and border of every packaged app's window,
/// and the apps it frames.
/// <para>
/// It owns each such app's top-level window, so every way of going from a window to its process -- a process's
/// <c>MainWindowTitle</c>, <c>GetWindowThreadProcessId</c> on the window under the cursor -- answers with the frame host
/// rather than the app. It runs no .NET, so an attach to it waits for a runtime that never loads. An attach that names
/// it means the app inside the frame, and is resolved to that app where there is exactly one.
/// </para>
/// <para>
/// The app is the process owning the <c>Windows.UI.Core.CoreWindow</c> that is a child of the frame window. A minimized
/// app's frame has no such child: the core window is detached into a top-level window of its own, cloaked, under the
/// same title, so a frame with no child is matched to that window by its title.
/// </para>
/// </summary>
public static class FrameHost
{
	private const string ExecutableName = "ApplicationFrameHost";
	private const string FrameClass = "ApplicationFrameWindow";
	private const string CoreClass = "Windows.UI.Core.CoreWindow";

	/// <summary>
	/// The .NET apps framed by <paramref name="processId"/>, or null when it is not the frame host. Read from Windows
	/// everywhere but a test, through <see cref="BrokerOptions.FramedAppsOf"/>.
	/// </summary>
	public static IReadOnlyList<FramedApp>? AppsFramedBy(int processId)
	{
		if (!OperatingSystem.IsWindows() || !IsFrameHost(processId)) return null;

		try
		{
			return DotNetFramedBy(processId);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			// The desktop could not be read; the frame host is still known for what it is, and framing nothing it says so.
			return [];
		}
	}

	/// <summary>
	/// The process an attach to <paramref name="processId"/> is made to, and the sentence saying it was moved where it
	/// was: the one app the frame host frames, in place of the frame host.
	/// </summary>
	/// <param name="processId">The process the attach named.</param>
	/// <param name="framed">The .NET apps it frames, or null when it is not the frame host.</param>
	/// <exception cref="ArgumentException">It is the frame host, and frames no .NET app or several.</exception>
	public static (int ProcessId, string? Notice) Resolve(int processId, IReadOnlyList<FramedApp>? framed)
	{
		if (framed is null) return (processId, null);

		var what = $"pid {processId} is ApplicationFrameHost, which draws the frame around every packaged app's window and "
			+ "runs no .NET; a window found by its title belongs to it rather than to the app inside it.";

		if (framed is [var only])
		{
			return (only.ProcessId, $"{what} Attached to the app inside its one frame, '{only.Title}', pid {only.ProcessId}.");
		}

		if (framed.Count == 0)
		{
			throw new ArgumentException(
				$"{what} No .NET app has a window in it now. Attach to the app's own process, or launch it with "
					+ "rose_debug_launch_uwp.");
		}

		var listed = string.Join("; ", framed.Select(app => $"pid {app.ProcessId}, '{app.Title}'"));

		throw new ArgumentException(
			$"{what} It frames {framed.Count} .NET apps, so which one was meant is not known: {listed}. Attach to that app's "
				+ "own pid.");
	}

	private static bool IsFrameHost(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return string.Equals(process.ProcessName, ExecutableName, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
		{
			return false;
		}
	}

	/// <summary>
	/// The apps framed by the frame host that run .NET: a native app in a frame -- Settings, say -- is no target, and
	/// counting it would make one framed .NET app read as a choice between two.
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static List<FramedApp> DotNetFramedBy(int frameHostId) =>
		[.. FramedBy(frameHostId).Where(app => app.ProcessId != frameHostId && WindowsRunningApps.RunsDotNet(app.ProcessId))];

	[SupportedOSPlatform("windows")]
	private static List<FramedApp> FramedBy(int frameHostId)
	{
		var framed = new List<FramedApp>();
		var detachedFrames = new List<string>();
		var coreWindows = new List<(int ProcessId, string Title)>();

		EnumWindowsProc visit = (window, _) =>
		{
			var className = ClassName(window);

			if (className == CoreClass)
			{
				coreWindows.Add(((int)ProcessOf(window), Text(window)));
				return true;
			}

			if (className != FrameClass || ProcessOf(window) != frameHostId) return true;

			var title = Text(window);
			var core = FindWindowEx(window, IntPtr.Zero, CoreClass, null);

			if (core != IntPtr.Zero) framed.Add(new FramedApp((int)ProcessOf(core), title));
			else if (title.Length > 0) detachedFrames.Add(title);

			return true;
		};

		EnumWindows(visit, IntPtr.Zero);
		GC.KeepAlive(visit);

		foreach (var title in detachedFrames)
		{
			framed.AddRange(coreWindows
				.Where(core => string.Equals(core.Title, title, StringComparison.Ordinal))
				.Select(core => new FramedApp(core.ProcessId, title)));
		}

		return [.. framed.DistinctBy(app => app.ProcessId)];
	}

	[SupportedOSPlatform("windows")]
	private static uint ProcessOf(IntPtr window)
	{
		GetWindowThreadProcessId(window, out var processId);
		return processId;
	}

	[SupportedOSPlatform("windows")]
	private static string Text(IntPtr window)
	{
		var buffer = new char[512];
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

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(IntPtr window, [Out] char[] text, int maxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(IntPtr window, [Out] char[] className, int maxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
