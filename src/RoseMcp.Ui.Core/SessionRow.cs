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

	public void Update(LiveAppSessionSummary summary)
	{
		DisplayName = summary.TargetDescription;
		TargetProcessId = summary.TargetProcessId;

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

		IsFaulted = summary.State == LiveAppSessionState.Faulted;
		Detail = summary.Detail ?? string.Empty;
		IsEnded = summary.State == LiveAppSessionState.Ended;
		HostLogPath = summary.HostLogPath;

		_lastEventAge = summary.LastEventAge;
		_resumeDeadlineUtc = summary.Stop?.ResumeDeadlineUtc;
		_readAtUtc = DateTime.UtcNow;

		Merge(Running, summary.Running);
		Merge(Recent, summary.Recent);

		HasRunning = Running.Count > 0;
		HasRecent = Recent.Count > 0;
		RecentHeader = DescribeRecent(summary.Recent);

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

		ResumeLabel = _resumeDeadlineUtc is { } deadline
			? DescribeResume(IsHeld, Format.Countdown(deadline - utcNow))
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
	/// it is what a reader removes or moves; a step has none, which the wording says rather than
	/// leaving a blank where an id would be.
	/// </summary>
	public static string DescribeExecution(LiveAppSessionSummary summary)
	{
		if (summary.Stop is not { } stop) return string.Empty;

		var what = stop.State == LiveExecutionState.StoppedAtBreakpoint
			? stop.BreakpointId is { Length: > 0 } id ? $"breakpoint {id}" : "a breakpoint"
			: "a step";

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
	/// Whether a target's XAML can be inspected at all. Only the two frameworks with a diagnostics
	/// tap: WPF's live diagnostics are managed rather than a COM tap, and Unknown is not a framework.
	/// </summary>
	public static bool Inspectable(XamlStack stack) => stack is XamlStack.Uwp or XamlStack.WinUi;

	/// <summary>
	/// What can be inspected, or why nothing can, naming the target's framework and how that was
	/// decided.
	/// <para>
	/// The reason travels because <see cref="XamlStack.Unknown"/> has two causes worth telling apart
	/// -- modules that could not be read, and modules read and recognised as nothing -- and because a
	/// reader told only "no XAML tab" about a WinUI app they can see on screen would reasonably
	/// conclude the tool was broken.
	/// </para>
	/// </summary>
	public static string DescribeXaml(LiveAppSessionSummary summary)
	{
		var named = summary.XamlStack switch
		{
			XamlStack.Uwp => "UWP",
			XamlStack.WinUi => "WinUI 3",
			XamlStack.Wpf => "WPF",
			_ => "no XAML framework",
		};

		if (!Inspectable(summary.XamlStack))
		{
			return $"No visual tree: {named} ({summary.XamlStackReason}). Only UWP and WinUI targets can be inspected.";
		}

		var provider = summary.XamlProvider switch
		{
			LiveXamlProvider.Resident => "provider resident",
			LiveXamlProvider.Lost => "provider gone; the next read injects again",
			_ => "not injected yet",
		};

		return $"{named}{Format.Separator}{provider}";
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

	private static string DescribeResume(bool held, string countdown) => held
		? $"held for you{Format.Separator}{countdown} left"
		: $"auto-continues in {countdown}";
}
