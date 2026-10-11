using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The rules that decide which running processes a person is offered to attach to, apart from the Win32 that reads
/// them, so a test can reach them on any platform.
/// </summary>
public static class RunningApps
{
	/// <summary>
	/// The runtime modules that say a process runs .NET: CoreCLR, and the .NET Framework's two. A single-file app
	/// carries its runtime inside its own executable and loads none of them, so it is found only by the diagnostics
	/// pipe its runtime opens (<see cref="DiagnosticPipeProcessId"/>).
	/// </summary>
	public static IReadOnlyList<string> RuntimeModules { get; } = ["coreclr.dll", "clr.dll", "mscorwks.dll"];

	/// <summary>The prefix the .NET runtime's diagnostics pipe takes, which names the process that opened it.</summary>
	public const string DiagnosticPipePrefix = "dotnet-diagnostic-";

	/// <summary>Whether a process with these modules loaded runs .NET.</summary>
	public static bool RunsDotNet(IEnumerable<string> loadedModules) =>
		loadedModules.Any(module => RuntimeModules.Contains(module, StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// Whether a process is part of Rose -- the tray, the app, a worker, a live-app host -- named for its executable.
	/// Never offered: attaching to the app showing the list freezes the list, and to a host attaches a debugger to a
	/// debugger.
	/// </summary>
	public static bool IsRose(string processName) => processName.StartsWith("RoseMcp.", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The process a .NET diagnostics pipe belongs to, from its name -- <c>dotnet-diagnostic-{pid}-{stamp}-socket</c> --
	/// or null for any other pipe.
	/// </summary>
	public static int? DiagnosticPipeProcessId(string pipeName)
	{
		var name = pipeName[(pipeName.LastIndexOfAny(['\\', '/']) + 1)..];
		if (!name.StartsWith(DiagnosticPipePrefix, StringComparison.Ordinal)) return null;

		var rest = name[DiagnosticPipePrefix.Length..];
		var dash = rest.IndexOf('-', StringComparison.Ordinal);
		var digits = dash < 0 ? rest : rest[..dash];

		return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0
			? pid
			: null;
	}

	/// <summary>
	/// What a row calls a process: its window's title where it has one that says more than its name, else its name.
	/// </summary>
	public static string DisplayName(RunningApp app) =>
		app.WindowTitle is { Length: > 0 } title ? title : app.Name;

	/// <summary>Those with a window first, then by what a row calls them, then by process id.</summary>
	public static IReadOnlyList<RunningApp> Ordered(IEnumerable<RunningApp> apps) =>
	[
		.. apps
			.OrderByDescending(app => app.HasWindow)
			.ThenBy(DisplayName, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(app => app.ProcessId),
	];
}
