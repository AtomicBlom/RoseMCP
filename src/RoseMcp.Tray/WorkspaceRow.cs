using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;

using RoseMcp.Contracts;

namespace RoseMcp.Tray;

/// <summary>
/// One workspace as the window shows it. Formatting lives here rather than in XAML converters so
/// the numbers can be checked without standing up a UI.
/// </summary>
public sealed class WorkspaceRow : Observable
{
	private string _detail = string.Empty;
	private string _memory = string.Empty;
	private string _recentHeader = string.Empty;
	private Visibility _runningVisibility = Visibility.Collapsed;
	private Visibility _recentVisibility = Visibility.Collapsed;

	public WorkspaceRow(WorkspaceSummary summary)
	{
		SolutionPath = summary.SolutionPath;
		DisplayName = summary.DisplayName;

		Update(summary);
	}

	/// <summary>Identity of the row, and what the buttons act on. A worker's path never changes.</summary>
	public string SolutionPath { get; }

	public string DisplayName { get; }

	public string Detail
	{
		get => _detail;
		private set => Set(ref _detail, value);
	}

	public string Memory
	{
		get => _memory;
		private set => Set(ref _memory, value);
	}

	/// <summary>Operations in flight, in the order the worker started them.</summary>
	public ObservableCollection<ActivityRow> Running { get; } = [];

	/// <summary>Recently finished operations, newest first.</summary>
	public ObservableCollection<ActivityRow> Recent { get; } = [];

	public Visibility RunningVisibility
	{
		get => _runningVisibility;
		private set => Set(ref _runningVisibility, value);
	}

	public Visibility RecentVisibility
	{
		get => _recentVisibility;
		private set => Set(ref _recentVisibility, value);
	}

	public string RecentHeader
	{
		get => _recentHeader;
		private set => Set(ref _recentHeader, value);
	}

	public void Update(WorkspaceSummary summary)
	{
		Detail = Describe(summary);
		Memory = FormatMemory(summary);

		Merge(Running, summary.Running);
		Merge(Recent, summary.Recent);

		RunningVisibility = Running.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
		RecentVisibility = Recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
		RecentHeader = Recent.Count == 1 ? "1 finished operation" : $"{Recent.Count} finished operations";
	}

	/// <summary>
	/// Brings a list of rows in line with what the broker now reports, matching on activity id.
	/// <para>
	/// Rows are updated where they already exist rather than replaced, because a progress bar that
	/// is recreated four times a second never animates and an expander the reader opened closes
	/// under them.
	/// </para>
	/// </summary>
	private static void Merge(ObservableCollection<ActivityRow> rows, IReadOnlyList<WorkerActivity> activities)
	{
		for (var index = rows.Count - 1; index >= 0; index--)
		{
			if (!activities.Any(activity => activity.Id == rows[index].Id)) rows.RemoveAt(index);
		}

		for (var index = 0; index < activities.Count; index++)
		{
			var activity = activities[index];
			var existing = rows.FirstOrDefault(row => row.Id == activity.Id);

			if (existing is null)
			{
				rows.Insert(Math.Min(index, rows.Count), new ActivityRow(activity));
				continue;
			}

			existing.Update(activity);
		}
	}

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
