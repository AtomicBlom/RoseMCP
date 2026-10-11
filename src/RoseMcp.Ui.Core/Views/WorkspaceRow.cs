using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// One workspace as the Workspaces view shows it: solution and folder, who used it, what it was loaded under, one
/// line of health coloured only when somebody can act, its memory and how long it has gone unused -- and, opened,
/// its reasons, what the person accepted, what agents proposed, and why its load went the way it did.
/// <para>
/// Keyed by its solution path compared the way the broker compares it, so a row survives the list spelling the
/// path another way rather than being replaced with its detail closed. Its tone is its own, tested here, rather
/// than worked out in markup.
/// </para>
/// </summary>
public sealed class WorkspaceRow : Observable
{
	private string _solution = string.Empty;
	private string _folder = string.Empty;
	private string _usedBy = string.Empty;
	private string _usedByDetail = string.Empty;
	private string _configuration = string.Empty;
	private string _platform = string.Empty;
	private HealthLine _health = new(string.Empty, HealthTone.Quiet);
	private string _memory = string.Empty;
	private string _memoryDetail = string.Empty;
	private string _unused = string.Empty;
	private string _detailHeader = string.Empty;
	private string _exitReason = string.Empty;
	private bool _isAlive;
	private bool _isExpanded;
	private bool _isBusy;
	private string _problem = string.Empty;
	private string? _logPath;
	private bool _inUse;
	private DateTime _lastUsedUtc;
	private DateTime? _closesAtUtc;

	public WorkspaceRow(OperatorWorkspace workspace, IReadOnlyList<ActivityEntry> entries, DateTime utcNow)
	{
		Path = workspace.Summary.Workspace ?? workspace.Summary.DisplayName;

		Update(workspace, entries, utcNow);
	}

	/// <summary>The solution, absolute: the row's identity, and what every action on it names.</summary>
	public string Path { get; }

	/// <summary>The solution's file name.</summary>
	public string Solution
	{
		get => _solution;
		private set => Set(ref _solution, value);
	}

	/// <summary>The folder it is in, which opens in Explorer.</summary>
	public string Folder
	{
		get => _folder;
		private set => Set(ref _folder, value);
	}

	/// <summary>The agents whose calls the record holds for it: one by name, several by count, or nobody.</summary>
	public string UsedBy
	{
		get => _usedBy;
		private set => Set(ref _usedBy, value);
	}

	/// <summary>Each of them by name, for the tooltip.</summary>
	public string UsedByDetail
	{
		get => _usedByDetail;
		private set => Set(ref _usedByDetail, value);
	}

	/// <summary>The configuration it was loaded under.</summary>
	public string Configuration
	{
		get => _configuration;
		private set => Set(ref _configuration, value);
	}

	/// <summary>The platform it was loaded under.</summary>
	public string Platform
	{
		get => _platform;
		private set => Set(ref _platform, value);
	}

	/// <summary>Its health in one line.</summary>
	public string Health => _health.Text;

	public HealthTone Tone => _health.Tone;

	public bool IsCaution => Tone == HealthTone.Caution;

	public bool IsCritical => Tone == HealthTone.Critical;

	public bool IsWorking => Tone == HealthTone.Working;

	public bool IsQuiet => Tone == HealthTone.Quiet;

	/// <summary>What its worker holds, as a person reads memory: its working set.</summary>
	public string Memory
	{
		get => _memory;
		private set => Set(ref _memory, value);
	}

	/// <summary>Its working set, private bytes and managed heap, for the tooltip.</summary>
	public string MemoryDetail
	{
		get => _memoryDetail;
		private set => Set(ref _memoryDetail, value);
	}

	/// <summary>How long it has gone unused and when it closes, or that it is in use.</summary>
	public string Unused
	{
		get => _unused;
		private set => Set(ref _unused, value);
	}

	/// <summary>The opened row's first line: the path, what it was loaded under and when, and how many projects.</summary>
	public string DetailHeader
	{
		get => _detailHeader;
		private set => Set(ref _detailHeader, value);
	}

	/// <summary>Why the worker stopped, where it has.</summary>
	public string ExitReason
	{
		get => _exitReason;
		private set
		{
			Set(ref _exitReason, value);
			OnPropertyChanged(nameof(HasExitReason));
		}
	}

	public bool HasExitReason => ExitReason.Length > 0;

	/// <summary>Whether its worker is running. A stopped row can be reloaded, which starts it again, or closed.</summary>
	public bool IsAlive
	{
		get => _isAlive;
		private set => Set(ref _isAlive, value);
	}

	/// <summary>The worker's own log file, which Open its log opens.</summary>
	public string? LogPath
	{
		get => _logPath;
		private set
		{
			Set(ref _logPath, value);
			OnPropertyChanged(nameof(HasLog));
		}
	}

	public bool HasLog => LogPath is { Length: > 0 };

	/// <summary>The rebuilt analyzers, generators and code fixes it still answers from the old build of.</summary>
	public ObservableCollection<string> RebuiltAnalyzers { get; } = [];

	public bool HasRebuiltAnalyzers => RebuiltAnalyzers.Count > 0;

	/// <summary>The reasons in its health, each with what an agent is told.</summary>
	public ObservableCollection<ReasonRow> Reasons { get; } = [];

	public bool HasReasons => Reasons.Count > 0;

	/// <summary>The worker's reasons in its own words, where they carry no shape the reasons above give them.</summary>
	public ObservableCollection<string> UnshapedReasons { get; } = [];

	public bool HasUnshapedReasons => UnshapedReasons.Count > 0;

	/// <summary>Every acceptance the person has made for this solution, read when the row is opened.</summary>
	public ObservableCollection<AcceptedRow> Accepted { get; } = [];

	public bool HasAccepted => Accepted.Count > 0;

	/// <summary>The acceptances agents have proposed for this solution, waiting on the person.</summary>
	public ObservableCollection<ProposalRow> Proposals { get; } = [];

	public bool HasProposals => Proposals.Count > 0;

	/// <summary>Why its load went the way it did, read when asked.</summary>
	public ObservableCollection<LoadSection> Load { get; } = [];

	public bool HasLoad => Load.Count > 0;

	/// <summary>Whether the row is open.</summary>
	public bool IsExpanded
	{
		get => _isExpanded;
		set => Set(ref _isExpanded, value);
	}

	/// <summary>Whether something the person asked of this row is under way.</summary>
	public bool IsBusy
	{
		get => _isBusy;
		set => Set(ref _isBusy, value);
	}

	/// <summary>What went wrong with the last thing asked of this row, said on the row.</summary>
	public string Problem
	{
		get => _problem;
		set
		{
			Set(ref _problem, value);
			OnPropertyChanged(nameof(HasProblem));
		}
	}

	public bool HasProblem => Problem.Length > 0;

	/// <summary>Brings the row in line with the broker's row for it and the calls the record holds.</summary>
	public void Update(OperatorWorkspace workspace, IReadOnlyList<ActivityEntry> entries, DateTime utcNow)
	{
		var summary = workspace.Summary;

		Solution = summary.DisplayName;
		Folder = workspace.Folder;
		Configuration = workspace.Configuration ?? string.Empty;
		Platform = workspace.Platform ?? string.Empty;
		IsAlive = summary.Alive;
		ExitReason = summary.Alive ? string.Empty : summary.ExitReason;
		LogPath = summary.WorkerLogPath;

		_health = HealthLine.Of(workspace);
		OnPropertyChanged(nameof(Health));
		OnPropertyChanged(nameof(Tone));
		OnPropertyChanged(nameof(IsCaution));
		OnPropertyChanged(nameof(IsCritical));
		OnPropertyChanged(nameof(IsWorking));
		OnPropertyChanged(nameof(IsQuiet));

		var users = UsersOf(Path, entries);
		UsedBy = users.Count switch
		{
			0 => "nobody",
			1 => users[0],
			_ => $"{users.Count} sessions",
		};
		UsedByDetail = users.Count == 0
			? "No call the activity record holds was routed here."
			: $"Calls the activity record holds came from {string.Join(", ", users)}.";

		Memory = summary.WorkingSetBytes is { } workingSet ? Format.Bytes(workingSet) : "—";
		MemoryDetail = DescribeMemory(summary);
		DetailHeader = DescribeLoad(workspace);

		Replace(RebuiltAnalyzers, workspace.RebuiltAnalyzers);
		OnPropertyChanged(nameof(HasRebuiltAnalyzers));

		Rows.Merge(Reasons, summary.Health, row => row.Id, reason => reason.Id, reason => new ReasonRow(reason), (row, reason) => row.Update(reason));
		OnPropertyChanged(nameof(HasReasons));

		Replace(UnshapedReasons, summary.Health.Count == 0 ? summary.DegradedReasons : []);
		OnPropertyChanged(nameof(HasUnshapedReasons));

		_inUse = workspace.InUse;
		_lastUsedUtc = workspace.LastUsedUtc;
		_closesAtUtc = workspace.ClosesAtUtc;

		Tick(utcNow);
	}

	/// <summary>Moves the unused time and the close countdown on, counted from the row's last use rather than asked for.</summary>
	public void Tick(DateTime utcNow)
	{
		if (!IsAlive)
		{
			Unused = "—";
			return;
		}

		if (_inUse)
		{
			Unused = "in use";
			return;
		}

		var idle = utcNow - _lastUsedUtc;
		if (idle < TimeSpan.Zero) idle = TimeSpan.Zero;

		Unused = _closesAtUtc is { } closes
			? $"{Format.Uptime(idle)}{Format.Separator}closes in {Format.Countdown(closes - utcNow)}"
			: Format.Uptime(idle);
	}

	/// <summary>Shows the acceptances and proposals read when the row was opened.</summary>
	public void ShowDecisions(AcceptedReasons accepted, IReadOnlyList<ReasonProposal> proposals)
	{
		// Replaced rather than merged: neither holds anything a reader set, and an acceptance changed in what agents
		// read is the same key with other text.
		Accepted.Clear();
		foreach (var reason in accepted.Accepted) Accepted.Add(new AcceptedRow(reason));
		OnPropertyChanged(nameof(HasAccepted));

		Proposals.Clear();
		foreach (var proposal in proposals.Where(proposal => proposal.Workspace is { } path && PathCasing.Comparer.Equals(path, Path)))
		{
			Proposals.Add(new ProposalRow(proposal));
		}

		OnPropertyChanged(nameof(HasProposals));
	}

	/// <summary>Shows why the load went the way it did, from a status read when asked.</summary>
	public void ShowLoad(WorkspaceStatusReport status)
	{
		Replace(Load, LoadDetail.Of(status));
		OnPropertyChanged(nameof(HasLoad));
	}

	/// <summary>The state a workspace is in, in a word, for a chip that names it elsewhere.</summary>
	public static string StateWord(OperatorWorkspace workspace) => workspace.Summary.Alive
		? workspace.Summary.State switch
		{
			WorkspaceState.Loading => "loading",
			WorkspaceState.Degraded => "degraded",
			WorkspaceState.Faulted => "faulted",
			WorkspaceState.PendingUnload => "closing",
			WorkspaceState.Unloaded => "closed",
			_ => "ready",
		}
		: "stopped";

	/// <summary>
	/// The agents whose calls <paramref name="entries"/> hold for <paramref name="solution"/>, newest first, each once.
	/// "Used by" is not a field anywhere: the calls are the only record of who used what.
	/// </summary>
	public static IReadOnlyList<string> UsersOf(string solution, IEnumerable<ActivityEntry> entries) =>
		[.. entries
			.Where(entry => entry.Kind == ActivityEntryKind.Call && entry.Workspace is { } path && PathCasing.Comparer.Equals(path, solution))
			.OrderByDescending(entry => entry.StartedUtc)
			.Select(AgentNames.Of)
			.Distinct(StringComparer.Ordinal)];

	/// <summary>Brings a bound list of workspace rows in line with the broker's list, keyed by path as the broker compares paths.</summary>
	public static void Merge(
		ObservableCollection<WorkspaceRow> rows,
		IReadOnlyList<OperatorWorkspace> workspaces,
		IReadOnlyList<ActivityEntry> entries,
		DateTime utcNow,
		IEqualityComparer<string>? comparer = null) =>
		Rows.Merge(
			rows,
			workspaces,
			row => row.Path,
			workspace => workspace.Summary.Workspace ?? workspace.Summary.DisplayName,
			workspace => new WorkspaceRow(workspace, entries, utcNow),
			(row, workspace) => row.Update(workspace, entries, utcNow),
			comparer ?? PathCasing.Comparer);

	private static string DescribeMemory(WorkspaceSummary summary)
	{
		if (summary.WorkingSetBytes is null) return summary.Alive ? "Not sampled." : "The worker has stopped and holds nothing.";

		var facts = new List<string> { $"working set {Format.Bytes(summary.WorkingSetBytes.Value)}" };
		if (summary.PrivateMemoryBytes is { } privateBytes) facts.Add($"private {Format.Bytes(privateBytes)}");
		if (summary.ManagedHeapBytes is { } heap) facts.Add($"managed heap {Format.Bytes(heap)}");
		if (summary.ProcessId is { } pid) facts.Add(Format.Pid(pid));

		return string.Join(Format.Separator, facts);
	}

	private static string DescribeLoad(OperatorWorkspace workspace)
	{
		var summary = workspace.Summary;
		var facts = new List<string> { summary.Workspace ?? summary.DisplayName };

		if (workspace.Configuration is { Length: > 0 } configuration) facts.Add(configuration);
		if (workspace.Platform is { Length: > 0 } platform) facts.Add(platform);
		if (workspace.BuildProperties.Count > 0) facts.Add(string.Join(" ", workspace.BuildProperties));

		var started = DateTime.SpecifyKind(summary.StartedUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
		facts.Add(summary.LoadSeconds is { } seconds
			? $"loaded {started} in {seconds.ToString("0.#", CultureInfo.InvariantCulture)} s"
			: $"started {started}");

		if (summary.ProjectCount is { } projects) facts.Add(Format.Count(projects, "project"));
		if (summary.FailedProjects.Count > 0) facts.Add($"failed: {string.Join(", ", summary.FailedProjects)}");
		if (summary.BuildConfiguration is { Length: > 0 } built && workspace.Configuration is null) facts.Add(built);

		return string.Join(Format.Separator, facts);
	}

	private static void Replace<T>(ObservableCollection<T> rows, IReadOnlyList<T> values)
	{
		if (rows.SequenceEqual(values)) return;

		rows.Clear();
		foreach (var value in values) rows.Add(value);
	}
}
