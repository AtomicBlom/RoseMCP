using System.Collections.ObjectModel;
using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core;

/// <summary>
/// One debug session as a window shows it: what it is attached to, whether the target is running or
/// held, what can be inspected, and what the session is doing.
/// <para>
/// The counterpart to a workspace row, and free of WinUI for the same reason the rest of this
/// project is: the tray and the inspector both show these, and a test has to be able to reach the
/// wording. Formatting lives here rather than in XAML converters so it can be read, and changed,
/// without standing up a UI.
/// </para>
/// </summary>
public sealed class SessionRow : Observable
{
	private string _stateLabel = string.Empty;
	private bool _isStarting;
	private bool _isHealthy;
	private bool _isCaution;
	private bool _isCritical;
	private bool _isNeutral;
	private string _facts = string.Empty;
	private string _xamlFact = string.Empty;
	private bool _hasXaml;
	private bool _isStopped;
	private string _executionLabel = string.Empty;
	private string _resumeLabel = string.Empty;
	private bool _isHeld;
	private string _heartbeat = string.Empty;
	private bool _isFaulted;
	private string _detail = string.Empty;
	private bool _isEnded;
	private string? _hostLogPath;
	private long _stopSequence;
	private bool _hasRunning;
	private bool _hasRecent;
	private string _recentHeader = string.Empty;
	private string _reportAge = string.Empty;
	private string _installLocation = string.Empty;
	private TimeSpan? _infoAge;
	private string _owners = string.Empty;
	private bool _isPersons;
	private string? _sharedWithClientId;

	// What Tick recomputes between polls, and when the poll they came from answered. An age is only
	// true at the instant it was read, so ticking it means adding what has passed since.
	private TimeSpan? _lastEventAge;
	private DateTime _readAtUtc = DateTime.UtcNow;
	private DateTime? _resumeDeadlineUtc;

	public SessionRow(LiveAppSessionSummary summary)
	{
		SessionId = summary.SessionId;

		Update(summary);
	}

	/// <summary>Identity of the row, and what every action on it names. A session's id never changes.</summary>
	public string SessionId { get; }

	public string DisplayName { get; private set; } = string.Empty;

	/// <summary>
	/// The process being debugged, when the session has one.
	/// <para>
	/// Carried because it is what an inspector window is one of: the launcher has to name the
	/// process rather than the session, so that detaching and attaching again reaches the window
	/// already open on that program.
	/// </para>
	/// </summary>
	public int? TargetProcessId { get; private set; }

	public string StateLabel
	{
		get => _stateLabel;
		private set => Set(ref _stateLabel, value);
	}

	public bool IsStarting
	{
		get => _isStarting;
		private set => Set(ref _isStarting, value);
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

	/// <summary>Target process, host process, architecture and uptime, in one line.</summary>
	public string Facts
	{
		get => _facts;
		private set => Set(ref _facts, value);
	}

	/// <summary>
	/// What can be inspected about this target's UI, or why nothing can. Said either way, because
	/// the absence of a XAML surface is a fact about the target rather than a missing feature.
	/// </summary>
	public string XamlFact
	{
		get => _xamlFact;
		private set => Set(ref _xamlFact, value);
	}

	/// <summary>Whether a XAML surface can be offered at all. False for a target that has none.</summary>
	public bool HasXaml
	{
		get => _hasXaml;
		private set => Set(ref _hasXaml, value);
	}

	/// <summary>
	/// Whether the target is held by the debugger. Frames, locals and threads are readable only
	/// while it is, and a XAML request cannot be served at all.
	/// </summary>
	public bool IsStopped
	{
		get => _isStopped;
		private set => Set(ref _isStopped, value);
	}

	public string ExecutionLabel
	{
		get => _executionLabel;
		private set => Set(ref _executionLabel, value);
	}

	/// <summary>When the target moves again, and what will move it. Empty while it is running.</summary>
	public string ResumeLabel
	{
		get => _resumeLabel;
		private set => Set(ref _resumeLabel, value);
	}

	/// <summary>Whether a person is holding this stop, so the safety timer is suspended.</summary>
	public bool IsHeld
	{
		get => _isHeld;
		private set => Set(ref _isHeld, value);
	}

	/// <summary>How long ago the target last produced an event, which is how a wedged app shows.</summary>
	public string Heartbeat
	{
		get => _heartbeat;
		private set => Set(ref _heartbeat, value);
	}

	public bool IsFaulted
	{
		get => _isFaulted;
		private set => Set(ref _isFaulted, value);
	}

	/// <summary>Why the session is faulted, when it is.</summary>
	public string Detail
	{
		get => _detail;
		private set => Set(ref _detail, value);
	}

	/// <summary>
	/// Whether the session is over. Its rows stay readable, and everything that would act on the
	/// target is not offered.
	/// </summary>
	public bool IsEnded
	{
		get => _isEnded;
		private set => Set(ref _isEnded, value);
	}

	/// <summary>The host's own log file, so a reader can open the log that explains this session.</summary>
	public string? HostLogPath
	{
		get => _hostLogPath;
		private set => Set(ref _hostLogPath, value);
	}

	/// <summary>
	/// The stop's identity, or zero while the target runs. A reader watching this sees a new stop
	/// when it changes, which is what says the frames and locals it holds are of a stop that is over.
	/// </summary>
	public long StopSequence
	{
		get => _stopSequence;
		private set => Set(ref _stopSequence, value);
	}

	/// <summary>The thread the stop is on, where the runtime named one; null while the target runs.</summary>
	public int? StopThreadId { get; private set; }

	/// <summary>
	/// How old the host's report of this session is, counted on between polls. What makes the rest of the row
	/// readable: a stop, a state or a XAML fact from a report a minute old is a minute old, and a host that has
	/// stopped reporting looks exactly like one with nothing new to say unless the age is shown.
	/// </summary>
	public string ReportAge
	{
		get => _reportAge;
		private set => Set(ref _reportAge, value);
	}

	/// <summary>When the session attached or launched, local time.</summary>
	public string Attached { get; private set; } = string.Empty;

	/// <summary>Where a packaged target was installed from; empty for any other target.</summary>
	public string InstallLocation
	{
		get => _installLocation;
		private set
		{
			Set(ref _installLocation, value);
			OnPropertyChanged(nameof(HasInstallLocation));
		}
	}

	public bool HasInstallLocation => InstallLocation.Length > 0;

	/// <summary>Calls in flight against this session, in the order they started.</summary>
	public ObservableCollection<ActivityRow> Running { get; } = [];

	/// <summary>Recently finished calls, newest first.</summary>
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

	/// <summary>Who is in the session: who started it, and the agent session a person shared theirs with.</summary>
	public string Owners
	{
		get => _owners;
		private set => Set(ref _owners, value);
	}

	/// <summary>Whether the person started this session, which is what makes it theirs to share.</summary>
	public bool IsPersons
	{
		get => _isPersons;
		private set => Set(ref _isPersons, value);
	}

	/// <summary>The row of the agent session it is shared with, by the id the client table lists it under; null where it is shared with none.</summary>
	public string? SharedWithClientId
	{
		get => _sharedWithClientId;
		private set
		{
			Set(ref _sharedWithClientId, value);
			OnPropertyChanged(nameof(IsShared));
		}
	}

	/// <summary>Whether it is shared with an agent session.</summary>
	public bool IsShared => SharedWithClientId is not null;

	/// <summary>
	/// How old the host's report may get before the header stops calling the session live. The broker asks every host
	/// how it is once a second, so five seconds is several answers missed in a row rather than one slow one.
	/// </summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

	private string _inSession = string.Empty;
	private string _liveness = string.Empty;
	private bool _isLive;
	private string _stopTitle = string.Empty;
	private string _stopFacts = string.Empty;
	private string _setBy = string.Empty;
	private string _stoppedAt = string.Empty;
	private string _framework = string.Empty;
	private string _solution = string.Empty;
	private string _appLine = string.Empty;
	private LiveAppSessionState _state;

	/// <summary>Who is in the session, as the Inspector's header says it: you, and the agent session that started it or that you shared it with.</summary>
	public string InSession
	{
		get => _inSession;
		private set => Set(ref _inSession, value);
	}

	/// <summary>Whether the host has answered lately enough for what the header says to be current.</summary>
	public bool IsLive
	{
		get => _isLive;
		private set => Set(ref _isLive, value);
	}

	/// <summary>"Live", or how long ago the host last answered when it has stopped answering, counted on between polls.</summary>
	public string Liveness
	{
		get => _liveness;
		private set => Set(ref _liveness, value);
	}

	/// <summary>Why the app is stopped, as the header's first words say it, or that it is running.</summary>
	public string StopTitle
	{
		get => _stopTitle;
		private set => Set(ref _stopTitle, value);
	}

	/// <summary>Who stopped it, when, and when it lets go, counted down between polls. Empty while it runs.</summary>
	public string StopFacts
	{
		get => _stopFacts;
		private set => Set(ref _stopFacts, value);
	}

	/// <summary>The process, its framework, its host and the solution its agent works in, in one line.</summary>
	public string AppLine
	{
		get => _appLine;
		private set => Set(ref _appLine, value);
	}

	/// <summary>The agent session that started it, where an agent did and the broker still lists it.</summary>
	public LiveAppAgent? StartedByAgent { get; private set; }

	/// <summary>The agent session a person shared it with, where they did.</summary>
	public LiveAppAgent? SharedWith { get; private set; }

	/// <summary>
	/// The agent sessions in this debug session, which are whom a note about something in it can go to: the one that
	/// started it, or the one a person shared theirs with.
	/// </summary>
	public IReadOnlyList<LiveAppAgent> Agents => new[] { StartedByAgent, SharedWith }.OfType<LiveAppAgent>().ToList();

	/// <summary>
	/// Takes the solution the session's agent last worked in, read from the activity record, for the line naming the
	/// app. Nothing in a debug session names a solution, so this is said as what it is: where the agent works.
	/// </summary>
	public void Learn(string? solution)
	{
		_solution = DescribeSolution(solution);
		AppLine = DescribeApp(Facts, _framework, _solution);
	}

	/// <summary>Who is in a session, as the Inspector's header says it.</summary>
	public static string DescribeParties(LiveAppSessionSummary summary)
	{
		if (summary.StartedBy != LiveAppStarter.Person)
		{
			var starter = summary.StartedByAgent?.Name is { Length: > 0 } name ? name : "an agent session";
			return $"In this session: you, and {starter} (started it)";
		}

		if (summary.SharedWith is not { } share) return "In this session: you (started it), not shared with an agent";

		var shared = share.Name is { Length: > 0 } named ? named : share.ClientId;
		var gone = share.Connected == false ? " (not connected)" : string.Empty;

		return $"In this session: you (started it), and {shared}{gone}";
	}

	/// <summary>
	/// Whether a session's report is current, and if not how old it is: "Live" while the host answers, its age once it
	/// has gone quiet past <see cref="StaleAfter"/>. A host that has stopped answering looks exactly like one with nothing
	/// new to say unless this is shown.
	/// </summary>
	public static (bool Live, string Text) DescribeLiveness(LiveAppSessionState state, TimeSpan? reportAge)
	{
		if (state == LiveAppSessionState.Ended) return (false, "Ended");
		if (state == LiveAppSessionState.Faulted) return (false, "Faulted");
		if (reportAge is not { } age) return (false, "waiting for the host's first report");

		return age < StaleAfter ? (true, "Live") : (false, $"host last answered {Format.Age(age)}");
	}

	/// <summary>Why the app is stopped, in the header's words: at a breakpoint, after a step, paused; or running.</summary>
	public static string DescribeStop(LiveAppSessionSummary summary) => summary.Stop?.State switch
	{
		null => summary.State == LiveAppSessionState.Ended ? "Ended" : "Running",
		LiveExecutionState.StoppedAtBreakpoint => "Stopped at a breakpoint",
		LiveExecutionState.PausedByOperator => "Paused",
		_ => "Stopped after a step",
	};

	/// <summary>
	/// Who stopped it, where the broker knows: who set the breakpoint, or you for a pause, since only a window pauses.
	/// Empty for a step, whose stepper the broker does not record, rather than a guess.
	/// </summary>
	public static string DescribeSetter(LiveAppSessionSummary summary)
	{
		if (summary.Stop?.State == LiveExecutionState.PausedByOperator) return "paused by you";
		if (summary.StopSetBy is not { } setter) return string.Empty;
		if (setter.Person) return "set by you";

		return setter.Agent?.Name is { Length: > 0 } name ? $"set by {name}" : "set by an agent session";
	}

	/// <summary>
	/// The framework a target runs, as the header names it; empty where it is none the debugger recognised.
	/// <paramref name="tap"/> is the name a dropped-in tap gives its framework, which is the only name there is for it.
	/// </summary>
	public static string DescribeFramework(XamlStack stack, string? tap = null) => stack switch
	{
		XamlStack.Uwp => "UWP",
		XamlStack.WinUi => "WinUI 3",
		XamlStack.Wpf => "WPF",
		XamlStack.Tap => tap ?? "a dropped-in tap's framework",
		_ => string.Empty,
	};

	/// <summary>The newest solution among <paramref name="entries"/>, which is where the agent that made them last worked.</summary>
	public static string? SolutionOf(IEnumerable<ActivityEntry> entries) =>
		entries
			.Where(entry => entry.Kind == ActivityEntryKind.Call && entry.Workspace is { Length: > 0 })
			.OrderByDescending(entry => entry.StartedUtc)
			.Select(entry => entry.Workspace)
			.FirstOrDefault();

	/// <summary>A solution as the header names it, file then folder; empty for none.</summary>
	public static string DescribeSolution(string? solution)
	{
		if (solution is not { Length: > 0 } path) return string.Empty;

		var cut = path.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']);

		return cut < 0 ? $"its agent works in {path}" : $"its agent works in {path[(cut + 1)..]} in {path[..cut]}";
	}

	private static string DescribeApp(string facts, string framework, string solution) =>
		string.Join(Format.Separator, new[] { facts, framework, solution }.Where(part => part.Length > 0));

	public void Update(LiveAppSessionSummary summary)
	{
		DisplayName = summary.TargetDescription;
		TargetProcessId = summary.TargetProcessId;

		Owners = DescribeOwners(summary);
		IsPersons = summary.StartedBy == LiveAppStarter.Person;
		SharedWithClientId = summary.SharedWith?.ClientId;

		var tone = ToneOf(summary);
		StateLabel = DescribeState(summary);
		IsStarting = tone == Tone.Starting;
		IsHealthy = tone == Tone.Healthy;
		IsCaution = tone == Tone.Caution;
		IsCritical = tone == Tone.Critical;
		IsNeutral = tone == Tone.Neutral;

		Facts = DescribeFacts(summary);
		XamlFact = DescribeXaml(summary);
		HasXaml = Inspectable(summary.XamlStack);

		IsStopped = summary.Stop is not null;
		ExecutionLabel = DescribeExecution(summary);
		IsHeld = summary.Stop?.Resume == LiveStopResume.HeldByOperator;
		StopSequence = summary.Stop?.EventSequence ?? 0;
		StopThreadId = summary.Stop?.ThreadId;

		IsFaulted = summary.State == LiveAppSessionState.Faulted;
		Detail = summary.Detail ?? string.Empty;
		IsEnded = summary.State == LiveAppSessionState.Ended;
		HostLogPath = summary.HostLogPath;

		InstallLocation = summary.InstallLocation ?? string.Empty;
		Attached = DateTime.SpecifyKind(summary.StartedUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

		_infoAge = summary.InfoAge;
		_lastEventAge = summary.LastEventAge;
		_resumeDeadlineUtc = summary.Stop?.ResumeDeadlineUtc;
		_readAtUtc = DateTime.UtcNow;

		Merge(Running, summary.Running);
		Merge(Recent, summary.Recent);

		HasRunning = Running.Count > 0;
		HasRecent = Recent.Count > 0;
		RecentHeader = DescribeRecent(summary.Recent);

		_state = summary.State;
		StartedByAgent = summary.StartedByAgent;
		SharedWith = summary.SharedWith;
		InSession = DescribeParties(summary);
		StopTitle = DescribeStop(summary);
		_setBy = DescribeSetter(summary);
		_stoppedAt = summary.Stop is { } stop
			? $"stopped {DateTime.SpecifyKind(stop.StoppedAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}"
			: string.Empty;
		_framework = DescribeFramework(summary.XamlStack, summary.XamlTap);
		AppLine = DescribeApp(Facts, _framework, _solution);

		Tick(_readAtUtc);
	}

	/// <summary>
	/// Recomputes the two labels that move on their own, without asking the broker anything.
	/// <para>
	/// A heartbeat and a countdown are wrong the moment they are rendered, and both are what a reader
	/// watches when something has gone quiet -- a session polled every second would otherwise show an
	/// age that steps in whole seconds and a deadline that only moves when something else does.
	/// </para>
	/// </summary>
	public void Tick(DateTime utcNow)
	{
		var since = utcNow - _readAtUtc;

		Heartbeat = _lastEventAge is { } age ? $"last event {Format.Age(age + since)}" : "no events yet";
		ReportAge = _infoAge is { } info ? $"reported {Format.Age(info + since)}" : "no report from the host yet";

		ResumeLabel = _resumeDeadlineUtc is { } deadline
			? DescribeResume(IsHeld, deadline - utcNow)
			: string.Empty;

		var (live, liveness) = DescribeLiveness(_state, _infoAge + since);
		IsLive = live;
		Liveness = liveness;

		StopFacts = IsStopped
			? string.Join(Format.Separator, new[] { _setBy, _stoppedAt, ResumeLabel }.Where(part => part.Length > 0))
			: string.Empty;
	}

	/// <summary>Brings a list of activity rows in line with what the broker now reports.</summary>
	private static void Merge(ObservableCollection<ActivityRow> rows, IReadOnlyList<WorkerActivity> activities) =>
		Rows.Merge(
			rows,
			activities,
			row => row.Id,
			activity => activity.Id,
			activity => new ActivityRow(activity),
			(row, activity) => row.Update(activity));

	/// <summary>The colour a session is drawn in.</summary>
	public enum Tone
	{
		Starting,
		Healthy,
		Caution,
		Critical,
		Neutral,
	}

	/// <summary>
	/// A held target is Caution rather than Healthy: an app frozen by a debugger is a state somebody
	/// has to end, and a session that has stopped answering its poll is one whose answers have gone
	/// stale without saying so.
	/// </summary>
	public static Tone ToneOf(LiveAppSessionSummary summary) => summary.State switch
	{
		LiveAppSessionState.Starting => Tone.Starting,
		LiveAppSessionState.Faulted => Tone.Critical,
		LiveAppSessionState.Ended => Tone.Neutral,
		_ => summary.Stop is not null ? Tone.Caution : Tone.Healthy,
	};

	public static string DescribeState(LiveAppSessionSummary summary) => summary.State switch
	{
		LiveAppSessionState.Starting => "Starting",
		LiveAppSessionState.Faulted => "Faulted",
		LiveAppSessionState.Ended => "Ended",
		_ => summary.Stop is not null ? "Stopped" : "Running",
	};

	/// <summary>
	/// What stopped the target and where, or nothing while it runs. The breakpoint is named because
	/// it is what a reader removes or moves; a step and a manual pause have none, which the wording
	/// says rather than leaving a blank where an id would be.
	/// </summary>
	public static string DescribeExecution(LiveAppSessionSummary summary)
	{
		if (summary.Stop is not { } stop) return string.Empty;

		var what = stop.State switch
		{
			LiveExecutionState.StoppedAtBreakpoint =>
				stop.BreakpointId is { Length: > 0 } id ? $"breakpoint {id}" : "a breakpoint",
			LiveExecutionState.PausedByOperator => "paused",
			_ => "a step",
		};

		return stop.ThreadId is { } thread ? $"{what} on thread {thread}" : what;
	}

	/// <summary>Target, host, architecture and uptime. What a reader needs to find the process.</summary>
	public static string DescribeFacts(LiveAppSessionSummary summary)
	{
		var facts = new List<string>
		{
			Format.Pid(summary.TargetProcessId),
			summary.HostProcessId is { } host ? $"host {host}" : "no host",
			summary.Architecture.ToString().ToLowerInvariant(),
			$"up {Format.Uptime(summary.Uptime)}",
		};

		return string.Join(Format.Separator, facts);
	}

	/// <summary>
	/// Whether a target's UI can be inspected at all: the two frameworks RoseMCP ships a tap for, and any
	/// framework a dropped-in tap serves. WPF has no tap, and Unknown is not a framework.
	/// </summary>
	public static bool Inspectable(XamlStack stack) => stack is XamlStack.Uwp or XamlStack.WinUi or XamlStack.Tap;

	/// <summary>
	/// What can be inspected, or why nothing can, naming the target's framework and how that was
	/// decided.
	/// <para>
	/// The reason travels because <see cref="XamlStack.Unknown"/> has two causes worth telling apart
	/// -- modules that could not be read, and modules read and recognised as nothing -- and because a
	/// reader told only "no XAML view" about a WinUI app they can see on screen would reasonably
	/// conclude the tool was broken.
	/// </para>
	/// </summary>
	public static string DescribeXaml(LiveAppSessionSummary summary)
	{
		var named = summary.XamlStack == XamlStack.Unknown ? "no XAML framework" : DescribeFramework(summary.XamlStack, summary.XamlTap);

		if (!Inspectable(summary.XamlStack))
		{
			return $"No visual tree: {named} ({summary.XamlStackReason}). Only UWP and WinUI targets, and those a dropped-in tap serves, can be inspected.";
		}

		var provider = summary.XamlProvider switch
		{
			LiveXamlProvider.Resident => "provider resident",
			LiveXamlProvider.Lost => "provider gone; the next read injects again",
			_ => "not injected yet",
		};

		return $"{named}{Format.Separator}{provider}";
	}

	/// <summary>
	/// Who is in the session, as a header says it: an agent's, or yours alone, or yours and the agent session you
	/// shared it with -- named as it was named when shared, and said to be gone while the client table lists it no
	/// more, since the share then waits for that session's relay to come back.
	/// </summary>
	public static string DescribeOwners(LiveAppSessionSummary summary)
	{
		if (summary.StartedBy != LiveAppStarter.Person) return "Started by an agent";

		if (summary.SharedWith is not { } share) return "Started by you · not shared with an agent";

		var name = share.Name is { Length: > 0 } named ? named : share.ClientId;
		var gone = share.Connected == false ? " (not connected)" : string.Empty;

		return $"Started by you · shared with {name}{gone}";
	}

	/// <summary>Failures get a count of their own, because they are the reason history is kept at all.</summary>
	public static string DescribeRecent(IReadOnlyList<WorkerActivity> recent)
	{
		var header = Format.Count(recent.Count, "recent call");
		var failed = recent.Count(activity => activity.Outcome == ActivityOutcome.Failed);

		return failed == 0 ? header : $"{header}, {failed} failed";
	}

	/// <summary>
	/// Whether a XAML apply finished between two polls, so a window showing the tree knows it is
	/// looking at a tree that has moved.
	/// <para>
	/// Read from the activity history rather than from a notification, because an apply an agent made
	/// is the case that matters and nothing tells a window about that. A completed call appearing in
	/// the recent list is the only signal either side has.
	/// </para>
	/// </summary>
	public static bool AppliedXaml(LiveAppSessionSummary before, LiveAppSessionSummary after)
	{
		var already = before.Recent
			.Where(activity => activity.Operation == ToolNames.LiveAppXamlApply)
			.Select(activity => activity.Id)
			.ToHashSet();

		return after.Recent.Any(activity =>
			activity.Operation == ToolNames.LiveAppXamlApply
			&& activity.Outcome == ActivityOutcome.Succeeded
			&& !already.Contains(activity.Id));
	}

	/// <summary>
	/// How long before this session's stop ends, deferred to the one place that says it, so the RoseMCP
	/// window and the Inspector's header cannot word the same deadline two ways.
	/// </summary>
	private static string DescribeResume(bool held, TimeSpan remaining) =>
		Inspector.HoldCountdown.Describe(held, remaining);
}
