using RoslynMcp.Contracts;

namespace RoslynMcp.Tray;

/// <summary>
/// One workspace as the window shows it. Formatting lives here rather than in XAML converters so
/// the numbers can be checked without standing up a UI.
/// </summary>
public sealed class WorkspaceRow(WorkspaceSummary summary)
{
	public string DisplayName { get; } = summary.DisplayName;

	public string SolutionPath { get; } = summary.SolutionPath;

	public string Detail { get; } = Describe(summary);

	public string Memory { get; } = FormatMemory(summary);

	/// <summary>
	/// Working set is the headline because it is the number Task Manager shows and the one people
	/// compare against. The managed heap sits beside it because for a Roslyn host the gap between
	/// them is mostly compilation caches, which is the interesting part.
	/// </summary>
	public static string FormatMemory(WorkspaceSummary summary)
	{
		if (summary.WorkingSetBytes is not { } workingSet) return "--";

		var heap = summary.ManagedHeapBytes is { } managed ? $" ({Bytes(managed)} heap)" : string.Empty;
		return Bytes(workingSet) + heap;
	}

	public static string Describe(WorkspaceSummary summary)
	{
		var state = summary.Alive ? "running" : summary.ExitReason.ToLowerInvariant();
		var process = summary.ProcessId is { } id ? $"pid {id}" : "no process";

		return $"{state} - {process} - up {FormatUptime(summary.Uptime)}";
	}

	private static string FormatUptime(TimeSpan uptime) => uptime.TotalHours >= 1
		? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
		: uptime.TotalMinutes >= 1
			? $"{(int)uptime.TotalMinutes}m"
			: $"{(int)uptime.TotalSeconds}s";

	private static string Bytes(long value)
	{
		const double Mega = 1024 * 1024;
		const double Giga = Mega * 1024;

		return value >= Giga ? $"{value / Giga:F1} GB" : $"{value / Mega:F0} MB";
	}
}
