using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Settings;
using RoseMcp.Ui;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Inspector;

/// <summary>
/// An Inspector window, which is about one debugged process once it has one: the app, the agent and you in one view.
/// <para>
/// The header says which app, who is in the session and how current that is, and why the app stopped, who stopped
/// it and when it lets go, with what moves it. Below it the code view -- threads, any thread's stack, the chosen
/// frame's source, locals and watches -- or the XAML or breakpoints view, beside a stream of the app's events, the
/// agents' calls into the session and the person's notes. Anything a person can select there can be asked about,
/// in a note to the agent session in the debug session.
/// </para>
/// <para>
/// One per process rather than one per machine, so the taskbar tells two debugged apps apart and the title says which
/// is which. Opened on its own it is about nothing yet and shows its start page; it becomes about the process a person
/// attaches to or launches there, or the one whose session they open. When the app exits the window stays on it,
/// saying so and naming the session's log, until the person goes back to the start page or attaches again; a session
/// the person detached here goes straight back to the start page. There is deliberately no list of sessions to
/// switch between: the RoseMCP window's Debugging view is where sessions are listed. It reads through the app's one
/// client, which it does not own and so does not dispose.
/// </para>
/// </summary>
public sealed partial class InspectorWindow : Window
{
	/// <summary>
	/// How often the session is re-read. A second is what the broker's own refresh of each host runs at, so the
	/// header is never more than a poll behind the host's last answer.
	/// </summary>
	private static readonly TimeSpan SessionInterval = TimeSpan.FromSeconds(1);

	/// <summary>
	/// How long the close waits for the hold to be given back. Two seconds is several times what the round trip takes
	/// and short enough that nobody reads it as a window refusing to close.
	/// </summary>
	private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(2);

	/// <summary>How many of an agent session's newest calls are read to find the solution it works in.</summary>
	private const int SolutionCalls = 50;

	// Logical pixels. Wide enough for threads and a stack beside source and values beside the stream, which is the
	// widest thing this window shows; the minimum keeps the three columns readable.
	private const int InitialWidth = 1280;
	private const int InitialHeight = 840;
	private const int MinimumWidth = 960;
	private const int MinimumHeight = 560;

	private readonly OperatorClient _client;
	private readonly PollLoop _sessionPoll;
	private readonly DispatcherQueueTimer _ticking;

	/// <summary>Cancels what the window asks on its own account -- the agent sessions for a note, a solution -- when it closes.</summary>
	private readonly CancellationTokenSource _lifetime = new();

	/// <summary>The note being written, one for the window, about whatever was last asked about.</summary>
	private readonly NoteComposer _composer = new();

	private InspectedSession? _inspected;
	private SessionRow? _row;

	/// <summary>
	/// The last summary read, kept only so the next one can be compared with it. An agent's live edit completing in the
	/// activity list is the only signal either side has that the tree this window is showing has been changed by
	/// somebody else.
	/// </summary>
	private LiveAppSessionSummary? _summary;

	/// <summary>The session this window is reading, once it has one; null on the start page and once its session has ended.</summary>
	private string? _sessionId;

	/// <summary>The process this window is about, as the launch named it, or as the session started on its start page reports it.</summary>
	private int? _targetProcessId;

	/// <summary>
	/// Brings forward another window already about a session, where there is one, so opening a session from the start
	/// page reaches the window showing it rather than a second window about the same process.
	/// </summary>
	private readonly Func<string, int?, bool> _shownElsewhere;

	/// <summary>
	/// The one hold on the target. On the window rather than in a control because the host has one hold: two holders
	/// each taking their own would race, and the loser would be a person whose target resumed for no reason they could see.
	/// </summary>
	private HoldKeeper? _holds;

	/// <summary>Whether the close has already run its release, so the second one goes through.</summary>
	private bool _closing;

	/// <summary>Whether the session this window shows has ended, and the window is saying so rather than reading it.</summary>
	private bool _ended;

	/// <summary>Whether this window detached its own session, which goes back to the start page rather than saying it ended.</summary>
	private bool _detaching;

	/// <summary>The row last selected anywhere in the window, which the shortcut asks about.</summary>
	private NoteSubject? _regarded;

	/// <summary>The agent session whose solution the header names, so it is read once per agent rather than per poll.</summary>
	private string? _solutionOf;

	/// <summary>What the window said when the broker stopped answering, so the first answer after can take it down.</summary>
	private string? _offlineNotice;

	/// <summary>What the window said when its session went, so opening the next one can take it down.</summary>
	private string? _goneNotice;

	/// <param name="client">The app's client, shared by every window.</param>
	/// <param name="sessionId">The session to open on, or null to settle on the one session there is.</param>
	/// <param name="targetProcessId">The process the session debugs, where the launch named it.</param>
	/// <param name="optionsProblem">Why the app's command line could not be read, to say on screen.</param>
	/// <param name="shownElsewhere">Brings forward another window about a session and says so, or says there is none.</param>
	public InspectorWindow(
		OperatorClient client,
		string? sessionId,
		int? targetProcessId,
		string? optionsProblem,
		Func<string, int?, bool> shownElsewhere)
	{
		InitializeComponent();

		_client = client;
		_sessionId = sessionId;
		_targetProcessId = targetProcessId;
		_shownElsewhere = shownElsewhere;

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(TitleBarArea);
		WindowChrome.ApplySize(this, InitialWidth, InitialHeight, MinimumWidth, MinimumHeight);
		// The lens rather than the rose: this window and the RoseMCP window sit side by side in the taskbar, and the
		// icon is what somebody picks between them by.
		WindowChrome.ApplyIcon(this, "rose-inspector.ico", "rose-inspector.png", TitleMark);

		AskComposer.Composer = _composer;

		Code.Attach(_client, Report, Ask, Regard, location => Execution.Locate(location));
		Stream.Attach(_client, StreamFailed, Ask, Regard);
		Breakpoints.Attach(_client, Report, Ask);
		Breakpoints.Counted += (_, count) => BreakpointsView.Text = count == 0 ? "Breakpoints" : $"Breakpoints ({count})";
		Execution.Attach(_client, Report);
		Execution.DetachRequested += OnDetach;
		Xaml.Attach(_client, Report);
		Start.Attach(_client, Report, Open);

		_sessionPoll = new PollLoop(RefreshAsync, SessionInterval, Report);

		// A second timer for the labels that move without anything being asked: how current the host's report is, and
		// how long until the stop lets go. Folding these into the poll would make them step in poll-sized jumps, and
		// they are exactly what a reader watches when they suspect an app has stopped answering.
		_ticking = DispatcherQueue.CreateTimer();
		_ticking.Interval = TimeSpan.FromSeconds(1);
		_ticking.Tick += (_, _) => Tick();
		_ticking.Start();

		if (optionsProblem is { } problem) ShowNotice(problem, InfoBarSeverity.Warning);

		if (_sessionId is null) ShowStartPage();
		else ShowEmpty(InspectorText.WaitingForSession(_sessionId), string.Empty);

		_sessionPoll.Start();
		_ = CheckBrokerBuildAsync();

		AppWindow.Closing += OnWindowClosing;

		// Everything this window reads stops with it: the session poll, the stream's three long polls, the breakpoint
		// lists, the visual tree, and any read of a stop or a watch in flight. A read left running against a closed
		// window keeps asking the broker questions nobody will read. The client is the app's, and stays.
		Closed += (_, _) =>
		{
			_ticking.Stop();
			_sessionPoll.Stop();
			_lifetime.Cancel();
			Start.Showing(false);
			Code.Stop();
			Stream.Stop();
			Breakpoints.Stop();
			Xaml.Showing(false);
		};
	}

	/// <summary>
	/// Asks the broker which build it is, once, and warns where it is not this inspector's build. A broker that cannot
	/// be reached says nothing here: the session poll says that too, and in more useful words.
	/// </summary>
	private async Task CheckBrokerBuildAsync()
	{
		try
		{
			var hello = await _client.HelloAsync(_lifetime.Token);
			var warning = InspectorText.AnotherBuild(hello.Build, BuildIdentity.Of(typeof(InspectorWindow).Assembly));

			// Not over a notice already showing, which is about the command line this window was started with and
			// matters more.
			if (warning is not null && !Notice.IsOpen) ShowNotice(warning, InfoBarSeverity.Warning);
		}
		catch (Exception exception) when (exception is OperatorException or OperationCanceledException or ObjectDisposedException)
		{
			// Unreachable, or the window closed first; the session poll says the first, and nobody is left to read the
			// second.
		}
	}

	/// <summary>
	/// Points this window at a session, which a redirected launch does. Only ever the same process this window is
	/// already about, because the single-instance key is that process -- or, for a window on its start page or showing
	/// a session that ended, the process the launch names, which it becomes about. Named no session, a window showing
	/// one that ended goes to its start page, which is what "Debug an app" brought it forward for.
	/// </summary>
	public void ShowSession(string? sessionId, int? targetProcessId = null)
	{
		if (string.IsNullOrWhiteSpace(sessionId))
		{
			if (_ended) ShowStartPage();
			return;
		}

		if (sessionId == _sessionId) return;

		_sessionId = sessionId;
		_targetProcessId ??= targetProcessId;
		_inspected = null;
		_row = null;

		HideStartPage();
		ShowEmpty(InspectorText.WaitingForSession(sessionId), string.Empty);
		_sessionPoll.Kick();

		// The last session's going is not news about this one.
		if (_goneNotice is { } gone && Notice.IsOpen && Notice.Message == gone) Notice.IsOpen = false;
		_goneNotice = null;
	}

	/// <summary>
	/// Whether this window is about no live session -- on its start page, or showing one that ended -- which "Debug an
	/// app" brings forward rather than opening another.
	/// </summary>
	public bool IsOnStartPage => _sessionId is null;

	/// <summary>
	/// What the start page hands over once a session is started or opened there: another window's, where one is
	/// already about it, and this window's otherwise.
	/// </summary>
	private void Open(DebugStart started)
	{
		var session = started.Session;
		if (_shownElsewhere(session.SessionId, session.TargetProcessId)) return;

		_targetProcessId = session.TargetProcessId;
		ShowSession(session.SessionId);

		// A process has one debugger, so attaching to one already being debugged opened that session; said, so a
		// person who expected a session of their own is not left wondering why it is an agent's.
		if (started.Existing) ShowNotice(StartPage.AlreadyDebugged(session), InfoBarSeverity.Informational);
	}

	/// <summary>
	/// Whether this window is the one about <paramref name="targetProcessId"/>, else about <paramref name="sessionId"/>.
	/// The process first, because detaching and attaching again is the same program being looked at, and should reach
	/// the same window.
	/// </summary>
	public bool IsAbout(string? sessionId, int? targetProcessId)
	{
		var process = _targetProcessId ?? _row?.TargetProcessId;
		if (targetProcessId is { } pid && process is not null) return process == pid;

		return sessionId is { Length: > 0 } && string.Equals(sessionId, _sessionId, StringComparison.Ordinal);
	}

	/// <summary>Brings this window forward, which is what a second Inspect click is asking for.</summary>
	public void BringToFront()
	{
		AppWindow.Show();
		Activate();
	}

	/// <summary>
	/// Reads the session this window is about. A window about none is on its start page, which reads for itself, or
	/// is showing a session that ended, which has nothing more to say.
	/// <para>
	/// A session that is no longer listed has ended: the window stays on it, saying so -- unless the person detached it
	/// here, when it goes back to the start page they asked for by detaching.
	/// </para>
	/// </summary>
	private async Task RefreshAsync(CancellationToken cancellationToken)
	{
		try
		{
			if (_sessionId is not { } sessionId)
			{
				Online();
				return;
			}

			Show(await _client.SessionAsync(sessionId, cancellationToken));
			Online();
		}
		catch (OperatorException failure) when (failure.Failure == OperatorFailure.NotFound)
		{
			Online();

			if (_row is not null && !_detaching)
			{
				EnterEnded();
				return;
			}

			LeaveForStartPage(_sessionId);
		}
		catch (OperatorException failure)
		{
			Offline(failure);
		}
	}

	/// <summary>Goes back to the start page from a session that has gone, saying it went.</summary>
	private void LeaveForStartPage(string? gone)
	{
		_sessionId = null;
		_inspected = null;
		_row = null;
		_detaching = false;

		ShowStartPage();

		_goneNotice = InspectorText.SessionGone(gone ?? "?");
		ShowNotice(_goneNotice, InfoBarSeverity.Informational);
	}

	/// <summary>
	/// Shows the start page in place of everything about a session: no header, views or stream, and the title of a
	/// window about nothing. The page reads for itself while it is on screen.
	/// </summary>
	private void ShowStartPage()
	{
		_sessionId = null;
		_inspected = null;
		_row = null;

		ShowEmpty(string.Empty, string.Empty);
		EmptyState.Visibility = Visibility.Collapsed;
		Header.Visibility = Visibility.Collapsed;
		Views.Visibility = Visibility.Collapsed;
		StreamFrame.Visibility = Visibility.Collapsed;

		Start.Visibility = Visibility.Visible;
		Start.Showing(true);
	}

	/// <summary>Takes the start page down, cancelling what it was reading, for a window that is now about a session.</summary>
	private void HideStartPage()
	{
		if (Start.Visibility == Visibility.Collapsed) return;

		Start.Showing(false);
		Start.Visibility = Visibility.Collapsed;
		Header.Visibility = Visibility.Visible;
		Views.Visibility = Visibility.Visible;
	}

	/// <summary>Binds the views and the stream to the session, the first time it is seen, and renders the header.</summary>
	private void Show(LiveAppSessionSummary summary)
	{
		if (_row is null || _row.SessionId != summary.SessionId)
		{
			_row = new SessionRow(summary);
			_inspected = new InspectedSession(summary.SessionId);
			_ended = false;
			_regarded = null;
			_solutionOf = null;

			// One keeper per session, because a hold is about a target: carrying one across would let the bar give back
			// a hold on a process this window is no longer about.
			var sessionId = summary.SessionId;
			_holds = new HoldKeeper(
				(seconds, release, cancellationToken) => _client.HoldAsync(sessionId, seconds, release, cancellationToken),
				Report);

			Code.Bind(_inspected);
			Stream.Bind(_inspected);
			Breakpoints.Bind(_inspected);
			Execution.Bind(_inspected, _holds);
			Xaml.Bind(_inspected);

			EmptyState.Visibility = Visibility.Collapsed;
			EndedPanel.Visibility = Visibility.Collapsed;
			Execution.Visibility = Visibility.Visible;
			StreamFrame.Visibility = Visibility.Visible;
			Views.Visibility = Visibility.Visible;
			ShowView();
		}
		else
		{
			// Before the row takes the new summary, because what it compares is one summary against the one before it
			// -- and an apply completing is the only signal either side has that an agent changed the tree this window
			// is showing.
			if (_summary is { } previous && SessionRow.AppliedXaml(previous, summary)) Xaml.AppliedXaml();

			_row.Update(summary);
		}

		_summary = summary;
		ShowHeader(_row);
		LearnSolution(_row);

		if (_row.IsEnded)
		{
			if (_detaching) LeaveForStartPage(summary.SessionId);
			else EnterEnded();

			return;
		}

		// The XAML stack is probed lazily off the target's module list, so a session can gain its view a second or two
		// after it first appears.
		if (_row.HasXaml != (XamlView.Visibility == Visibility.Visible)) ShowView();

		// Before the views, because a hold belongs to the stop the target is at now: one taken at the last stop was
		// cleared when the target continued.
		_ = _holds?.AtStop(_row.StopSequence);

		Code.Observe(_row);
		Execution.Observe(_row);
		Xaml.Observe(_row);
	}

	/// <summary>
	/// Says the session has ended and stops reading it, keeping what was read on screen: the header says the app
	/// exited, or that the session ended, and names its log. Every read stops here -- the stream's polls, the stop's
	/// reads, the breakpoint lists, the visual tree -- since a session that has ended has nothing more to say, and the
	/// session poll stops asking about it. The stream's tail is read once more first, waiting for nothing, since the
	/// event saying the app exited can still be in flight when the poll sees the end.
	/// </summary>
	private async void EnterEnded()
	{
		if (_ended) return;

		_ended = true;
		_sessionId = null;
		_detaching = false;
		var noticed = DateTimeOffset.UtcNow;
		var row = _row;

		Code.Stop(InspectorText.SessionEnded);
		Breakpoints.Stop();
		Breakpoints.Showing(false);
		Xaml.Showing(false);
		Execution.End(release: false);

		// Nothing is held on a target that has gone, and nothing should try to give it back.
		_holds = null;

		Execution.Visibility = Visibility.Collapsed;
		EndedPanel.Visibility = Visibility.Visible;
		ShareButton.Visibility = Visibility.Collapsed;

		XamlView.Visibility = Visibility.Collapsed;
		if (Views.SelectedItem == XamlView) CodeView.IsSelected = true;
		ShowView();

		EndedTitle.Text = InspectorText.Ended(null, noticed);
		EndedLog.Text = InspectorText.EndedLog(row?.HostLogPath);
		LiveText.Text = "Ended";
		LiveDot.Fill = Brush("SystemFillColorNeutralBrush");

		if (row is not null)
		{
			TitleText.Text = $"{row.DisplayName} (ended) — RoseMCP Inspector";
			Title = TitleText.Text;
		}

		var exited = await Stream.FinishAsync(_lifetime.Token);

		// Still the same ended session: a new one may have been opened in this window while the tail was read.
		if (_ended && ReferenceEquals(row, _row)) EndedTitle.Text = InspectorText.Ended(exited, noticed);
	}

	private void OnViewChosen(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowView();

	/// <summary>
	/// Shows the chosen view and hides the rest, and tells the two that poll whether they are the visible one: a view
	/// that cannot be seen must not be polling on the reader's behalf. The XAML view is offered only for a live target
	/// whose framework the provider can serve; the header says why for the rest.
	/// </summary>
	private void ShowView()
	{
		var bound = _inspected is not null;

		XamlView.Visibility = _row?.HasXaml == true && !_ended ? Visibility.Visible : Visibility.Collapsed;

		// A view that has gone cannot stay selected, or the window shows nothing at all.
		if (XamlView.Visibility == Visibility.Collapsed && Views.SelectedItem == XamlView) CodeView.IsSelected = true;

		var code = bound && Views.SelectedItem == CodeView;
		var breakpoints = bound && Views.SelectedItem == BreakpointsView;
		var xaml = bound && Views.SelectedItem == XamlView;

		Code.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
		Breakpoints.Visibility = breakpoints ? Visibility.Visible : Visibility.Collapsed;
		Xaml.Visibility = xaml ? Visibility.Visible : Visibility.Collapsed;

		Breakpoints.Showing(breakpoints && !_ended);
		Xaml.Showing(xaml && !_ended);
	}

	private void ShowHeader(SessionRow row)
	{
		SessionName.Text = row.DisplayName;
		AppLine.Text = row.AppLine;
		ToolTipService.SetToolTip(AppLine, row.AppLine.Length > 0
			? "The process, its framework, its host, and the solution the agent session in this debug session last worked in: nothing in a debug session names a solution, and the source shown comes from the app's own symbols."
			: null);
		InSessionText.Text = row.InSession;
		XamlFactText.Text = row.XamlFact;

		if (!_ended)
		{
			LiveText.Text = row.Liveness;
			LiveDot.Fill = Brush(row.IsLive ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
			ShareButton.Visibility = row.IsPersons ? Visibility.Visible : Visibility.Collapsed;
			ShareButton.Content = row.IsShared ? "Shared" : "Share";

			// The window's own name, and the taskbar's. One inspector per debugged process only helps somebody pick
			// between two of them if each says which process it is.
			TitleText.Text = $"{row.DisplayName} — RoseMCP Inspector";
			Title = TitleText.Text;
		}

		OpenLogButton.IsEnabled = row.HostLogPath is not null;
	}

	/// <summary>
	/// Reads, once per agent session, the solution that session last worked in, for the line naming the app. Nothing
	/// in a debug session names one, so the header says it as where the agent works rather than claiming it for the app.
	/// </summary>
	private async void LearnSolution(SessionRow row)
	{
		if ((row.StartedByAgent ?? row.SharedWith) is not { } agent || agent.ClientId == _solutionOf) return;

		_solutionOf = agent.ClientId;

		try
		{
			var page = await _client.ActivityAsync(
				new ActivityFilter { Client = agent.ClientId, Limit = SolutionCalls }, before: null, after: null, waitSeconds: 0, _lifetime.Token);

			if (!ReferenceEquals(row, _row)) return;

			row.Learn(SessionRow.SolutionOf(page.Entries));
			ShowHeader(row);
		}
		catch (Exception exception) when (exception is OperatorException or OperationCanceledException)
		{
			// The line goes without a solution: a broker that cannot answer is said by the session poll, and a window
			// that closed has nobody to tell.
		}
	}

	private void Tick()
	{
		// The same clock that moves the countdown is what renews the hold behind it, so what a reader sees and what
		// keeps the target still cannot disagree about how long is left.
		_ = _holds?.Tick(DateTime.UtcNow);

		if (_row is not { } row || _ended) return;

		row.Tick(DateTime.UtcNow);
		ShowHeader(row);
		Execution.Tick(row);
	}

	/// <summary>
	/// Gives the hold back before the window goes, and waits for it.
	/// <para>
	/// Waiting is the point. Releasing on <c>Closed</c> cancels the request it just made, so the target stays stopped
	/// until the host's own cap expires -- which is somebody's application frozen for minutes because a window was
	/// closed. The window declines to close once, releases, then closes for real. Budgeted, because a window that will
	/// not close is worse than a target that frees itself in a few minutes.
	/// </para>
	/// </summary>
	private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		// A session that has ended holds nothing: its target is gone, and asking its host would only wait out the budget.
		if (_closing || _ended || _holds is not { } holds) return;

		args.Cancel = true;
		_closing = true;

		await Task.WhenAny(holds.ReleaseAsync(), Task.Delay(ReleaseBudget));

		Close();
	}

	/// <summary>
	/// What to show when the broker is not answering. The views are left alone rather than cleared: a broker that
	/// restarts comes back in a second, and emptying the window each time would make a blink of unreachability look
	/// like the session ending.
	/// </summary>
	private void Offline(OperatorException failure)
	{
		var detail = failure.Failure switch
		{
			OperatorFailure.Unauthorized when !_client.HasToken => InspectorText.NoToken,
			OperatorFailure.Unauthorized => InspectorText.TokenRefused,
			_ => failure.Message,
		};

		ShowNotice(detail, InfoBarSeverity.Warning);
		_offlineNotice = detail;
	}

	/// <summary>
	/// Takes down what <see cref="Offline"/> said once the broker answers again, and only that: a notice about
	/// something else stays.
	/// </summary>
	private void Online()
	{
		if (_offlineNotice is null) return;

		if (Notice.IsOpen && Notice.Message == _offlineNotice) Notice.IsOpen = false;
		_offlineNotice = null;
	}

	/// <summary>
	/// A read of the stream that failed. A broker not answering is said the way the session poll says it, so the first
	/// answer after takes it down; anything else is the stream's own failure, put in front of the reader.
	/// </summary>
	private void StreamFailed(Exception exception)
	{
		var brokerGone = exception is OperatorException
		{
			Failure: OperatorFailure.Unreachable or OperatorFailure.Unauthorized or OperatorFailure.TimedOut,
		};

		if (brokerGone) Offline((OperatorException)exception);
		else Report(exception);
	}

	/// <summary>Shows a waiting or empty state in place of a session, stopping everything that read the last one.</summary>
	private void ShowEmpty(string title, string detail)
	{
		EmptyTitle.Text = title;
		EmptyDetail.Text = detail;
		EmptyState.Visibility = Visibility.Visible;

		Code.Visibility = Visibility.Collapsed;
		Breakpoints.Visibility = Visibility.Collapsed;
		Xaml.Visibility = Visibility.Collapsed;
		StreamFrame.Visibility = Visibility.Collapsed;
		EndedPanel.Visibility = Visibility.Collapsed;

		// The session going away is the case where a read would otherwise be left asking about a process this window
		// is no longer showing.
		Code.Stop();
		Stream.Stop();
		Breakpoints.Stop();
		Breakpoints.Showing(false);
		Xaml.Showing(false);
		Execution.End();

		_ended = false;
		XamlView.Visibility = Visibility.Collapsed;
		BreakpointsView.Text = "Breakpoints";
		Execution.Visibility = Visibility.Collapsed;

		OpenLogButton.IsEnabled = false;
		SessionName.Text = "No session";
		AppLine.Text = string.Empty;
		InSessionText.Text = string.Empty;
		LiveText.Text = string.Empty;
		XamlFactText.Text = string.Empty;
		ShareButton.Visibility = Visibility.Collapsed;
		TitleText.Text = "RoseMCP Inspector";
		Title = TitleText.Text;
	}

	/// <summary>
	/// Puts a failure a view hit in front of the reader. Views report through here rather than each growing an InfoBar,
	/// so two views cannot argue about what the window is saying.
	/// </summary>
	private void Report(Exception exception)
	{
		var severity = exception is OperatorException { Failure: OperatorFailure.Refused }
			? InfoBarSeverity.Warning
			: InfoBarSeverity.Error;

		ShowNotice(exception.Message, severity);
	}

	private void ShowNotice(string message, InfoBarSeverity severity)
	{
		Notice.Severity = severity;
		Notice.Message = message;
		Notice.IsOpen = true;
	}

	/// <summary>Notes the row last selected, which the shortcut asks about.</summary>
	private void Regard(NoteSubject? subject) => _regarded = subject;

	/// <summary>
	/// Opens the window's composer about <paramref name="subject"/>, beside <paramref name="anchor"/>, offering the
	/// agent sessions in this debug session. The note arrives with that session's next Rose call; nothing is promised
	/// sooner, since nothing pushes it.
	/// </summary>
	private void Ask(NoteSubject subject, FrameworkElement anchor)
	{
		if (_row is not { } row) return;

		_composer.Regard(subject);
		if (!_composer.IsOpen) _composer.Toggle();
		_composer.Offer([], "Finding the agent sessions in this debug session…");

		AskFlyout.ShowAt(anchor);
		_ = OfferAsync(row);
	}

	/// <summary>Offers the composer the agent sessions in the debug session, read as it opens because sessions come and go.</summary>
	private async Task OfferAsync(SessionRow row)
	{
		try
		{
			var clients = await _client.ClientsAsync(after: null, waitSeconds: 0, _lifetime.Token);
			var (recipients, unreachable) = AskAgent.Recipients(row.Agents, row.IsPersons, clients.Clients);

			_composer.Offer(recipients, unreachable);
		}
		catch (OperationCanceledException)
		{
			// The window closed.
		}
		catch (OperatorException exception)
		{
			_composer.Offer([], exception.Message);
		}
	}

	/// <summary>The shortcut: asks about whichever row was selected last.</summary>
	private void OnAskShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = true;

		if (_regarded is { } subject)
		{
			Ask(subject, Views);
			return;
		}

		ShowNotice(
			$"Select a thread, a frame, a value, a watch or a row of the stream first, then press {AskAgent.Shortcut} to ask an agent about it.",
			InfoBarSeverity.Informational);
	}

	private async void OnDetach(object? sender, EventArgs args)
	{
		if (_sessionId is not { } sessionId) return;

		Busy.IsActive = true;
		_detaching = true;

		try
		{
			var closed = await _client.DetachAsync(sessionId, CancellationToken.None);

			if (closed.DetachFailure is { } failure)
			{
				// Closed and still attached are not the same claim, and only the second is what detaching asked for:
				// an app left under a debugger nothing is driving is worse than either outcome on its own.
				ShowNotice($"The session closed, but the debugger did not come off the target: {failure}", InfoBarSeverity.Error);
			}

			_sessionPoll.Kick();
		}
		catch (Exception exception)
		{
			_detaching = false;
			Report(exception);
		}
		finally
		{
			Busy.IsActive = false;
		}
	}

	/// <summary>Leaves a session that ended for the start page.</summary>
	private void OnDebugAnother(object sender, RoutedEventArgs args) => ShowStartPage();

	/// <summary>
	/// Fills the share menu as it opens: each agent session behind a relay, the one it is shared with checked, and
	/// stopping where it is shared with one. Read as it opens rather than held, because sessions come and go.
	/// </summary>
	private async void OnShareOpening(object? sender, object args)
	{
		ShareMenu.Items.Clear();
		ShareMenu.Items.Add(new MenuFlyoutItem { Text = "Reading the agent sessions…", IsEnabled = false });

		try
		{
			var clients = await _client.ClientsAsync(after: null, waitSeconds: 0, _lifetime.Token);
			var choices = StartPage.ShareChoices(clients.Clients);
			var sharedWith = _row?.SharedWithClientId;

			ShareMenu.Items.Clear();

			foreach (var choice in choices)
			{
				var item = new ToggleMenuFlyoutItem { Text = choice.Name, Tag = choice.Id, IsChecked = choice.Id == sharedWith };
				item.Click += OnShareWith;
				ShareMenu.Items.Add(item);
			}

			if (choices.Count == 0)
			{
				ShareMenu.Items.Add(new MenuFlyoutItem { Text = "No agent session is connected through the relay", IsEnabled = false });
			}

			if (sharedWith is not null)
			{
				var stop = new MenuFlyoutItem { Text = "Stop sharing" };
				stop.Click += OnStopSharing;
				ShareMenu.Items.Add(new MenuFlyoutSeparator());
				ShareMenu.Items.Add(stop);
			}
		}
		catch (OperationCanceledException)
		{
			// The window closed.
		}
		catch (OperatorException exception)
		{
			ShareMenu.Items.Clear();
			Report(exception);
		}
	}

	private async void OnShareWith(object sender, RoutedEventArgs args)
	{
		if (_sessionId is not { } sessionId || sender is not FrameworkElement { Tag: string clientId }) return;

		try
		{
			Show(await _client.ShareAsync(sessionId, clientId, CancellationToken.None));
		}
		catch (OperatorException exception)
		{
			Report(exception);
		}
	}

	private async void OnStopSharing(object sender, RoutedEventArgs args)
	{
		if (_sessionId is not { } sessionId) return;

		try
		{
			Show(await _client.UnshareAsync(sessionId, CancellationToken.None));
		}
		catch (OperatorException exception)
		{
			Report(exception);
		}
	}

	/// <summary>
	/// Opens the host's own log, selected in Explorer where there is one and the folder otherwise. The log is where the
	/// answer is when a session has gone wrong in a way the window cannot show.
	/// </summary>
	private void OnOpenHostLog(object sender, RoutedEventArgs args)
	{
		try
		{
			var log = _row?.HostLogPath;

			if (log is not null && File.Exists(log))
			{
				Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log}\"") { UseShellExecute = true });
				return;
			}

			var folder = RoseLogFile.DirectoryFor("LiveApp");
			Directory.CreateDirectory(folder);
			Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
		}
		catch (Exception exception)
		{
			Report(exception);
		}
	}

	/// <summary>
	/// Reads the preference on opening rather than holding it, because another window writes this file too: a toggle
	/// showing what this window last set would disagree with the other's within a minute of somebody using it.
	/// </summary>
	private void OnSettingsOpening(object sender, object args) =>
		ShowOnAttach.IsChecked = RoseSettingsFile.Read().ShowInspectorOnAttach;

	/// <summary>
	/// Turns the "show the inspector when the debugger attaches" preference on or off, for every host on this machine.
	/// The item is put back to what the file says rather than to what was clicked, so a write that did not land shows
	/// as a toggle that did not move.
	/// </summary>
	private void OnToggleShowInspectorOnAttach(object sender, RoutedEventArgs args)
	{
		var wanted = sender is ToggleMenuFlyoutItem { IsChecked: true };

		if (!RoseSettingsFile.Write(RoseSettingsFile.Read() with { ShowInspectorOnAttach = wanted }))
		{
			ShowNotice(
				$"Could not write {RoseSettingsFile.PathFor()}, so that preference is unchanged.",
				InfoBarSeverity.Warning);
		}

		ShowOnAttach.IsChecked = RoseSettingsFile.Read().ShowInspectorOnAttach;
	}

	/// <summary>
	/// Shows the preferences file in Explorer. It is plain JSON and a person is allowed to read or edit it, which is
	/// most of the reason it is a file rather than something in the registry.
	/// </summary>
	private void OnOpenSettingsFile(object sender, RoutedEventArgs args)
	{
		try
		{
			var path = RoseSettingsFile.PathFor();

			if (!File.Exists(path))
			{
				// Written on demand, so "open the settings file" is never an item that opens nothing.
				RoseSettingsFile.Write(RoseSettingsFile.Read());
			}

			Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
		}
		catch (Exception exception)
		{
			Report(exception);
		}
	}

	/// <summary>A theme brush by its key, read as the theme has it now.</summary>
	private static Brush Brush(string key) =>
		Application.Current.Resources.TryGetValue(key, out var found) && found is Brush brush
			? brush
			: new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
