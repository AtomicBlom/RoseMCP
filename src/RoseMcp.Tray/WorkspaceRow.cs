using System.Collections.ObjectModel;

using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;

namespace RoseMcp.Tray;

/// <summary>
/// One workspace as the window shows it: what it is, what state it is in, what it costs, what it
/// is doing and what it has just done.
/// <para>
/// Formatting lives here rather than in XAML converters so it can be read without standing up a
/// UI, and every property is a plain value the template binds to directly. The tone flags are the
/// deliberate case: the template holds one element per tone, each carrying its own theme brush,
/// and shows the one that applies. A brush chosen in code would be the brush for whichever theme
/// was in force when it was chosen.
/// </para>
/// </summary>
public sealed class WorkspaceRow : Observable
{
	/// <summary>Between facts on one line. A middle dot reads as punctuation, where a dash reads as a range.</summary>
	public const string Separator = "  ·  ";

	private string _stateLabel = string.Empty;
	private bool _isLoading;
	private bool _isHealthy;
	private bool _isCaution;
	private bool _isCritical;
	private bool _isNeutral;
	private string _memory = string.Empty;
	private string _memoryDetail = string.Empty;
	private bool _hasMemoryDetail;
	private string _facts = string.Empty;
	private bool _hasHealth;
	private InfoBarSeverity _healthSeverity = InfoBarSeverity.Informational;
	private string _healthTitle = string.Empty;
	private string _healthMessage = string.Empty;
	private bool _hasNotice;
	private string _noticeMessage = string.Empty;
	private bool _hasRunning;
	private bool _hasRecent;
	private string _recentHeader = string.Empty;

	public WorkspaceRow(WorkspaceSummary summary)
	{
		SolutionPath = summary.SolutionPath;
		DisplayName = summary.DisplayName;

		Update(summary);
	}

	/// <summary>Identity of the row, and what the buttons act on. A worker's path never changes.</summary>
	public string SolutionPath { get; }

	public string DisplayName { get; }

	public string StateLabel
	{
		get => _stateLabel;
		private set => Set(ref _stateLabel, value);
	}

	public bool IsLoading
	{
		get => _isLoading;
		private set => Set(ref _isLoading, value);
	}

	public bool IsHealthy
	{
		get => _isHealthy;
		private set => Set(ref _isHealthy, value);
	}

	public bool IsCaution
	{
		get => _isCaution;
		private set => Set(ref _isCaution, value);
	}

	public bool IsCritical
	{
		get => _isCritical;
		private set => Set(ref _isCritical, value);
	}

	public bool IsNeutral
	{
		get => _isNeutral;
		private set => Set(ref _isNeutral, value);
	}

	/// <summary>Working set: the number Task Manager shows, and so the one people compare against.</summary>
	public string Memory
	{
		get => _memory;
		private set => Set(ref _memory, value);
	}

	/// <summary>
	/// The managed heap, beside the working set because for a Roslyn host the gap between them is
	/// mostly compilation caches, which is the interesting part.
	/// </summary>
	public string MemoryDetail
	{
		get => _memoryDetail;
		private set => Set(ref _memoryDetail, value);
	}

	public bool HasMemoryDetail
	{
		get => _hasMemoryDetail;
		private set => Set(ref _hasMemoryDetail, value);
	}

	/// <summary>Process, uptime, configuration, size and load time, in one line.</summary>
	public string Facts
	{
		get => _facts;
		private set => Set(ref _facts, value);
	}

	public bool HasHealth
	{
		get => _hasHealth;
		private set => Set(ref _hasHealth, value);
	}

	public InfoBarSeverity HealthSeverity
	{
		get => _healthSeverity;
		private set => Set(ref _healthSeverity, value);
	}

	public string HealthTitle
	{
		get => _healthTitle;
		private set => Set(ref _healthTitle, value);
	}

	public string HealthMessage
	{
		get => _healthMessage;
		private set => Set(ref _healthMessage, value);
	}

	public bool HasNotice
	{
		get => _hasNotice;
		private set => Set(ref _hasNotice, value);
	}

	public string NoticeMessage
	{
		get => _noticeMessage;
		private set => Set(ref _noticeMessage, value);
	}

	/// <summary>Operations in flight, in the order the worker started them.</summary>
	public ObservableCollection<ActivityRow> Running { get; } = [];

	/// <summary>Recently finished operations, newest first.</summary>
	public ObservableCollection<ActivityRow> Recent { get; } = [];

	public bool HasRunning
	{
		get => _hasRunning;
		private set => Set(ref _hasRunning, value);
	}

	public bool HasRecent
	{
		get => _hasRecent;
		private set => Set(ref _hasRecent, value);
	}

	public string RecentHeader
	{
		get => _recentHeader;
		private set => Set(ref _recentHeader, value);
	}

	public void Update(WorkspaceSummary summary)
	{
		var tone = ToneOf(summary);
		StateLabel = DescribeState(summary);
		IsLoading = tone == Tone.Loading;
		IsHealthy = tone == Tone.Healthy;
		IsCaution = tone == Tone.Caution;
		IsCritical = tone == Tone.Critical;
		IsNeutral = tone == Tone.Neutral;

		Memory = summary.WorkingSetBytes is { } workingSet ? Format.Bytes(workingSet) : "--";
		MemoryDetail = summary.ManagedHeapBytes is { } heap ? $"{Format.Bytes(heap)} heap" : string.Empty;
		HasMemoryDetail = MemoryDetail.Length > 0;
		Facts = DescribeFacts(summary);

		(HasHealth, HealthSeverity, HealthTitle, HealthMessage) = DescribeHealth(summary);
		NoticeMessage = string.Join(Environment.NewLine, summary.Notices);
		HasNotice = NoticeMessage.Length > 0;

		Merge(Running, summary.Running);
		Merge(Recent, summary.Recent);

		HasRunning = Running.Count > 0;
		HasRecent = Recent.Count > 0;
		RecentHeader = DescribeRecent(summary.Recent);
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

	/// <summary>The colour a workspace is drawn in. Five, because five things can be true of one.</summary>
	public enum Tone
	{
		Loading,
		Healthy,
		Caution,
		Critical,
		Neutral,
	}

	public static Tone ToneOf(WorkspaceSummary summary) => summary.State switch
	{
		WorkspaceState.Loading => Tone.Loading,
		WorkspaceState.Loaded => Tone.Healthy,
		WorkspaceState.Degraded or WorkspaceState.PendingUnload => Tone.Caution,
		WorkspaceState.Faulted => Tone.Critical,
		_ => Tone.Neutral,
	};

	public static string DescribeState(WorkspaceSummary summary) => summary.State switch
	{
		WorkspaceState.Loading => "Loading",
		WorkspaceState.Loaded => "Loaded",
		WorkspaceState.Degraded => "Degraded",
		WorkspaceState.PendingUnload => "Solution missing",
		WorkspaceState.Faulted => summary.Alive ? "Load failed" : "Crashed",
		_ => "Stopped",
	};

	public static string DescribeFacts(WorkspaceSummary summary)
	{
		var facts = new List<string>
		{
			summary.ProcessId is { } id ? $"pid {id}" : "no process",
			summary.Alive ? $"up {Format.Uptime(summary.Uptime)}" : DescribeExit(summary.ExitReason),
		};

		if (summary.BuildConfiguration is { Length: > 0 } configuration) facts.Add(configuration);
		if (summary.ProjectCount is { } projects) facts.Add(Format.Count(projects, "project"));

		var failed = summary.FailedProjects.Count;
		if (failed > 0) facts.Add($"{failed} failed to load");

		// Only once it has: a load that failed took some time too, and "loaded in" would be a lie.
		var loaded = summary.State is WorkspaceState.Loaded or WorkspaceState.Degraded;
		if (loaded && summary.LoadSeconds is { } seconds)
		{
			facts.Add($"loaded in {Format.Duration(TimeSpan.FromSeconds(seconds))}");
		}

		return string.Join(Separator, facts);
	}

	/// <summary>
	/// What to say about a workspace whose answers cannot be trusted, and how loudly. Nothing for
	/// one that is fine or merely stopped: the pill already says so, and an information bar under
	/// every card would leave the ones that matter with nothing to stand out against.
	/// </summary>
	public static (bool Has, InfoBarSeverity Severity, string Title, string Message) DescribeHealth(WorkspaceSummary summary)
	{
		var reasons = string.Join(Environment.NewLine + Environment.NewLine, summary.DegradedReasons);

		return summary.State switch
		{
			WorkspaceState.Faulted when !summary.Alive => (
				true,
				InfoBarSeverity.Error,
				"The worker crashed",
				"The next call on this solution starts a fresh one. Its log, under Open log folder, says why."),

			WorkspaceState.Faulted => (
				true,
				InfoBarSeverity.Error,
				"The solution did not load",
				reasons.Length > 0 ? reasons : "No reason was reported. The worker's log, under Open log folder, has the details."),

			WorkspaceState.Degraded => (true, InfoBarSeverity.Warning, "Answers may be incomplete", reasons),

			WorkspaceState.PendingUnload => (
				true,
				InfoBarSeverity.Warning,
				"The solution file is missing",
				"Answers come from the last good snapshot while it is gone, in case this is a branch switch that puts it straight back."),

			_ => (false, InfoBarSeverity.Informational, string.Empty, string.Empty),
		};
	}

	/// <summary>Failures get a count of their own, because they are the reason history is kept at all.</summary>
	public static string DescribeRecent(IReadOnlyList<WorkerActivity> recent)
	{
		var header = Format.Count(recent.Count, "recent operation");
		var failed = recent.Count(activity => activity.Outcome == ActivityOutcome.Failed);

		return failed == 0 ? header : $"{header}, {failed} failed";
	}

	private static string DescribeExit(string exitReason) => exitReason switch
	{
		"Crashed" => "crashed",
		"SolutionUnloaded" => "solution unloaded",
		"StoppedByBroker" => "stopped",
		_ => exitReason.ToLowerInvariant(),
	};
}
