using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Settings;
using RoseMcp.Ui;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.App.Inspector;

/// <summary>
/// An Inspector window, which is about one debugged process once it has one.
/// <para>
/// One per process rather than one per machine, so the taskbar tells two debugged apps apart and
/// the title says which is which. Opened on its own it is about nothing yet and shows its start
/// page -- the running apps to attach to, a launch form and the targets debugged lately -- and it
/// becomes about the process a person attaches to or launches there, or the one whose session they
/// open. It goes back to the start page when its session ends, still answering for that process,
/// so detaching and attaching again reaches the same window. There is deliberately no list of
/// sessions to switch between: the RoseMCP window's Debugging view is where sessions are listed, and
/// a window that could be re-pointed at another process while it shows one would make its own title
/// a lie. It reads through the app's one client, which it does not own and so does not dispose.
/// </para>
/// </summary>
public sealed partial class InspectorWindow : Window
{
	/// <summary>
	/// How often the session is re-read. A second is what the tray's own refresh runs at, so the
	/// two windows agree about what exists rather than one lagging the other by a poll.
	/// </summary>
	private static readonly TimeSpan SessionInterval = TimeSpan.FromSeconds(1);

	/// <summary>
	/// How long the close waits for the hold to be given back. Two seconds is several times what the
	/// round trip takes and short enough that nobody reads it as a window refusing to close.
	/// </summary>
	private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(2);

	// Logical pixels. Wide enough for a stack beside a variable tree, which is the widest thing
	// this window will have to show; the minimum keeps the header's two halves from colliding.
	private const int InitialWidth = 980;
	private const int InitialHeight = 720;
	private const int MinimumWidth = 640;
	private const int MinimumHeight = 480;

	private readonly OperatorClient _client;
	private readonly PollLoop _sessionPoll;
	private readonly DispatcherQueueTimer _ticking;

	private InspectedSession? _inspected;
	private SessionRow? _row;

	/// <summary>
	/// The last summary read, kept only so the next one can be compared with it. An agent's live edit
	/// completing in the activity list is the only signal either side has that the tree this window is
	/// showing has been changed by somebody else.
	/// </summary>
	private LiveAppSessionSummary? _summary;

	/// <summary>
	/// The session this window is about, once it has one. Set from the command line, from a
	/// redirected launch, or by what a person starts or opens on the start page.
	/// </summary>
	private string? _sessionId;

	/// <summary>The process this window is about, as the launch named it, or as the session started on its start page reports it.</summary>
	private int? _targetProcessId;

	/// <summary>
	/// Brings forward another window already about a session, where there is one, so opening a session from the
	/// start page reaches the window showing it rather than a second window about the same process.
	/// </summary>
	private readonly Func<string, int?, bool> _shownElsewhere;

	/// <summary>
	/// The one hold on the target, shared by the panes that read a stop. On the window rather than in
	/// a pane because the host has one hold: two panes each taking their own would race, and the loser
	/// would be a reader whose target resumed because somebody changed tab.
	/// </summary>
	private HoldKeeper? _holds;

	/// <summary>Whether the close has already run its release, so the second one goes through.</summary>
	private bool _closing;

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
		// The lens rather than the rose: this window and the tray sit side by side in the taskbar, and
		// the icon is what somebody picks between them by.
		WindowChrome.ApplyIcon(this, "rose-inspector.ico", "rose-inspector.png", TitleMark);

		Events.Attach(_client, Report);
		Breakpoints.Attach(_client, Report);
		Stack.Attach(_client, Report);
		Threads.Attach(_client, Report);
		Execution.Attach(_client, Report);
		Xaml.Attach(_client, Report);
		Start.Attach(_client, Report, Open);

		_sessionPoll = new PollLoop(RefreshAsync, SessionInterval, Report);

		// A second timer for the labels that move without anything being asked: how long ago the
		// target last breathed, and how long until it resumes itself. Folding these into the poll
		// would make the heartbeat step in poll-sized jumps, and the heartbeat is exactly what a
		// reader watches when they suspect an app has stopped.
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

		// Everything this window polls stops with it. A loop left running against a closed window
		// keeps asking the broker questions nobody will read. The client is the app's, and stays.
		Closed += (_, _) =>
		{
			_ticking.Stop();
			_sessionPoll.Stop();
			Start.Showing(false);
			Events.Showing(false);
			Breakpoints.Showing(false);
			Stack.Showing(false);
			Threads.Showing(false);
			Xaml.Showing(false);
		};
	}

	/// <summary>
	/// Asks the broker which build it is, once, and warns where it is not this inspector's build.
	/// <para>
	/// Once, because the inspector is launched by the tray it talks to, and a broker that changes
	/// build underneath a window has restarted, which the session poll already reports. A broker that
	/// cannot be reached says nothing here: the poll says that too, and in more useful words.
	/// </para>
	/// </summary>
	private async Task CheckBrokerBuildAsync()
	{
		try
		{
			var hello = await _client.HelloAsync(CancellationToken.None);
			var warning = InspectorText.AnotherBuild(hello.Build, BuildIdentity.Of(typeof(InspectorWindow).Assembly));

			// Not over a notice already showing, which is about the command line this window was
			// started with and matters more.
			if (warning is not null && !Notice.IsOpen) ShowNotice(warning, InfoBarSeverity.Warning);
		}
		catch (Exception exception) when (exception is OperatorException or ObjectDisposedException)
		{
			// Unreachable, or the window closed first; the session poll says the first, and nobody
			// is left to read the second.
		}
	}

	/// <summary>
	/// Points this window at a session, which a redirected launch does. Only ever the same process
	/// this window is already about, because the single-instance key is that process -- or, for a
	/// window on its start page, the process the launch names, which it becomes about.
	/// </summary>
	public void ShowSession(string? sessionId, int? targetProcessId = null)
	{
		if (string.IsNullOrWhiteSpace(sessionId) || sessionId == _sessionId) return;

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

	/// <summary>Whether this window is on its start page, about no session, which "Debug an app" brings forward rather than opening another.</summary>
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
	/// Whether this window is the one about <paramref name="targetProcessId"/>, else about
	/// <paramref name="sessionId"/>. The process first, because detaching and attaching again is the same program
	/// being looked at, and should reach the same window.
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
	/// Reads the session this window is about. A window about none is on its start page, which reads for
	/// itself, so there is nothing to read here; and one whose session has gone goes back to it, saying so.
	/// <para>
	/// A window is never settled on some session it was not pointed at: which process a person meant is
	/// what the start page asks them, rather than this window guessing and being confidently about the
	/// wrong program.
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
			var gone = _sessionId;
			_sessionId = null;
			_inspected = null;
			_row = null;

			Online();
			ShowStartPage();

			_goneNotice = InspectorText.SessionGone(gone ?? "?");
			ShowNotice(_goneNotice, InfoBarSeverity.Informational);
		}
		catch (OperatorException failure)
		{
			Offline(failure);
		}
	}

	/// <summary>
	/// Shows the start page in place of everything about a session: no header, tabs or panes, and the title of a
	/// window about nothing. The page reads for itself while it is on screen.
	/// </summary>
	private void ShowStartPage()
	{
		ShowEmpty(string.Empty, string.Empty);
		EmptyState.Visibility = Visibility.Collapsed;
		Header.Visibility = Visibility.Collapsed;
		Tabs.Visibility = Visibility.Collapsed;

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
		Tabs.Visibility = Visibility.Visible;
	}

	/// <summary>Binds the panes to the session, the first time it is seen, and renders the header.</summary>
	private void Show(LiveAppSessionSummary summary)
	{
		if (_row is null || _row.SessionId != summary.SessionId)
		{
			_row = new SessionRow(summary);
			_inspected = new InspectedSession(summary.SessionId);

			// One keeper per session, because a hold is about a target: carrying one across would let a
			// pane give back a hold on a process this window is no longer about.
			var sessionId = summary.SessionId;
			_holds = new HoldKeeper(
				(seconds, release, cancellationToken) => _client.HoldAsync(sessionId, seconds, release, cancellationToken),
				Report);

			Events.Bind(_inspected);
			Breakpoints.Bind(_inspected);
			Stack.Bind(_inspected, _holds);
			Threads.Bind(_inspected, _holds);
			Execution.Bind(_inspected, _holds);
			Xaml.Bind(_inspected);

			EmptyState.Visibility = Visibility.Collapsed;
			Execution.Visibility = Visibility.Visible;
			DetachButton.IsEnabled = true;
			ShowTab();
		}
		else
		{
			// Before the row takes the new summary, because what it compares is one summary against the
			// one before it -- and an apply completing is the only signal either side has that an agent
			// changed the tree this window is showing.
			if (_summary is { } previous && SessionRow.AppliedXaml(previous, summary)) Xaml.AppliedXaml();

			_row.Update(summary);
		}

		_summary = summary;
		ShowHeader(_row);

		// The XAML stack is probed lazily off the target's module list, so a session can gain its tab
		// a second or two after it first appears -- and losing one is what a session ending looks like.
		if (_row.HasXaml != (XamlTab.Visibility == Visibility.Visible)) ShowTab();

		// Before the panes, because the hold they are about to ask for belongs to the stop the target is
		// at now: one taken at the last stop was cleared when the target continued.
		_ = _holds?.AtStop(_row.StopSequence);

		// These act on a stop or on the tree rather than describing either: a sequence they have not
		// read yet is a new place the target is sitting, and a target that has stopped cannot answer
		// about its XAML at all.
		Stack.Observe(_row);
		Threads.Observe(_row);
		Execution.Observe(_row);
		Xaml.Observe(_row);
	}

	private void OnTabChosen(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowTab();

	/// <summary>
	/// Shows the chosen pane and hides the rest, and tells each whether it is the visible one --
	/// a pane that cannot see the screen must not be polling on the reader's behalf, and neither of
	/// the two that read a stop must be holding the target still for a tab nobody is looking at.
	/// <para>
	/// The XAML tab is present only for a target whose framework the provider can serve. The header
	/// says why for the rest, which is better than a tab that is there and answers nothing.
	/// </para>
	/// </summary>
	private void ShowTab()
	{
		var bound = _inspected is not null;

		XamlTab.Visibility = _row?.HasXaml == true ? Visibility.Visible : Visibility.Collapsed;

		// A tab that has gone cannot stay selected, or the window shows no pane at all.
		if (XamlTab.Visibility == Visibility.Collapsed && Tabs.SelectedItem == XamlTab) EventsTab.IsSelected = true;

		var events = bound && Tabs.SelectedItem == EventsTab;
		var breakpoints = bound && Tabs.SelectedItem == BreakpointsTab;
		var stack = bound && Tabs.SelectedItem == StackTab;
		var threads = bound && Tabs.SelectedItem == ThreadsTab;
		var xaml = bound && Tabs.SelectedItem == XamlTab;

		Events.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
		Breakpoints.Visibility = breakpoints ? Visibility.Visible : Visibility.Collapsed;
		Stack.Visibility = stack ? Visibility.Visible : Visibility.Collapsed;
		Threads.Visibility = threads ? Visibility.Visible : Visibility.Collapsed;
		Xaml.Visibility = xaml ? Visibility.Visible : Visibility.Collapsed;

		// All of them in one pass, in whatever order: the keeper reconciles after the pass rather than
		// on the first pane to speak, so the arriving pane's claim on the hold is already in place when
		// the leaving one gives its own up.
		Events.Showing(events);
		Breakpoints.Showing(breakpoints);
		Stack.Showing(stack);
		Threads.Showing(threads);
		Xaml.Showing(xaml);
	}

	private void ShowHeader(SessionRow row)
	{
		SessionName.Text = row.DisplayName;
		StateText.Text = row.StateLabel;
		FactsText.Text = row.Facts;
		XamlFactText.Text = row.XamlFact;
		OwnersText.Text = row.Owners;
		ShareButton.Visibility = row.IsPersons && !row.IsEnded ? Visibility.Visible : Visibility.Collapsed;
		ShareButton.Content = row.IsShared ? "Shared" : "Share";

		ExecutionPill.Visibility = row.IsStopped ? Visibility.Visible : Visibility.Collapsed;
		ExecutionText.Text = row.IsHeld ? $"{row.ExecutionLabel} · {row.ResumeLabel}" : row.ExecutionLabel;
		OpenLogButton.IsEnabled = row.HostLogPath is not null;

		// The window's own name, and the taskbar's. One inspector per debugged process only helps
		// somebody pick between two of them if each says which process it is.
		TitleText.Text = $"{row.DisplayName} — RoseMCP Inspector";
		Title = TitleText.Text;
	}

	private void Tick()
	{
		// The same clock that moves the countdown is what renews the hold behind it, so what a reader
		// sees and what keeps the target still cannot disagree about how long is left.
		_ = _holds?.Tick(DateTime.UtcNow);

		if (_row is not { } row) return;

		row.Tick(DateTime.UtcNow);
		ShowHeader(row);
	}

	/// <summary>
	/// Gives the hold back before the window goes, and waits for it.
	/// <para>
	/// Waiting is the point. Releasing on <c>Closed</c> and disposing the client in the same handler
	/// cancels the request it just made, so the target stays stopped until the host's own cap expires
	/// -- which is somebody's application frozen for minutes because a window was closed. The window
	/// declines to close once, releases, then closes for real.
	/// </para>
	/// <para>
	/// Budgeted, because a window that will not close is worse than a target that frees itself in a
	/// few minutes: a tray that has stopped answering must not take the inspector with it.
	/// </para>
	/// </summary>
	private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (_closing || _holds is not { } holds) return;

		args.Cancel = true;
		_closing = true;

		await Task.WhenAny(holds.ReleaseAsync(), Task.Delay(ReleaseBudget));

		Close();
	}

	/// <summary>
	/// What to show when the tray is not answering. The panes are left alone rather than cleared: a
	/// tray that restarts comes back in a second, and emptying the window each time would make a
	/// blink of unreachability look like the session ending.
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
	/// Takes down what <see cref="Offline"/> said once the broker answers again -- after a restart, or once the app
	/// has been handed the new run's token -- and only that: a notice about something else stays.
	/// </summary>
	private void Online()
	{
		if (_offlineNotice is null) return;

		if (Notice.IsOpen && Notice.Message == _offlineNotice) Notice.IsOpen = false;
		_offlineNotice = null;
	}

	private void ShowEmpty(string title, string detail)
	{
		EmptyTitle.Text = title;
		EmptyDetail.Text = detail;
		EmptyState.Visibility = Visibility.Visible;
		Events.Visibility = Visibility.Collapsed;
		Breakpoints.Visibility = Visibility.Collapsed;
		Stack.Visibility = Visibility.Collapsed;
		Threads.Visibility = Visibility.Collapsed;
		Xaml.Visibility = Visibility.Collapsed;
		Events.Showing(false);
		Breakpoints.Showing(false);

		// Both of these give the hold back on being hidden, which is what matters here: the session
		// going away is the case where a target would otherwise be left stopped with nothing watching.
		Stack.Showing(false);
		Threads.Showing(false);
		Xaml.Showing(false);

		// The tab goes with the session. Left up, it offers a visual tree for a process this window
		// is no longer about.
		XamlTab.Visibility = Visibility.Collapsed;

		// Nothing to pause or step, and the buttons would be about a process this window is no
		// longer showing.
		Execution.Visibility = Visibility.Collapsed;

		DetachButton.IsEnabled = false;
		OpenLogButton.IsEnabled = false;

		SessionName.Text = "No session";
		StateText.Text = string.Empty;
		FactsText.Text = string.Empty;
		XamlFactText.Text = string.Empty;
		OwnersText.Text = string.Empty;
		ShareButton.Visibility = Visibility.Collapsed;
		ExecutionPill.Visibility = Visibility.Collapsed;
		TitleText.Text = "RoseMCP Inspector";
		Title = TitleText.Text;
	}

	/// <summary>
	/// Puts a failure a pane hit in front of the reader. Panes report through here rather than each
	/// growing an InfoBar, so two panes cannot argue about what the window is saying.
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

	private async void OnDetach(object sender, RoutedEventArgs args)
	{
		if (_sessionId is not { } sessionId) return;

		Busy.IsActive = true;

		try
		{
			var closed = await _client.DetachAsync(sessionId, CancellationToken.None);

			if (closed.DetachFailure is { } failure)
			{
				// Closed and still attached are not the same claim, and only the second is what
				// detaching asked for: an app left under a debugger nothing is driving is worse
				// than either outcome on its own.
				ShowNotice($"The session closed, but the debugger did not come off the target: {failure}", InfoBarSeverity.Error);
			}
		}
		catch (Exception exception)
		{
			Report(exception);
		}
		finally
		{
			Busy.IsActive = false;
		}
	}

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
			var clients = await _client.ClientsAsync(after: null, waitSeconds: 0, CancellationToken.None);
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
	/// Opens the host's own log, selected in Explorer where there is one and the folder otherwise.
	/// The log is where the answer is when a session has gone wrong in a way the window cannot show.
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
	/// Reads the preference on opening rather than holding it, because the tray writes this file
	/// too: a toggle showing what this window last set would disagree with the tray's within a
	/// minute of somebody using the other one.
	/// </summary>
	private void OnSettingsOpening(object sender, object args) =>
		ShowOnAttach.IsChecked = RoseSettingsFile.Read().ShowInspectorOnAttach;

	/// <summary>
	/// Turns the "show the inspector when the debugger attaches" preference on or off, for every
	/// host on this machine.
	/// <para>
	/// The item is put back to what the file says rather than to what was clicked, so a write that
	/// did not land shows as a toggle that did not move. A checkbox claiming a preference the next
	/// attach will not honour is worse than one that visibly refused.
	/// </para>
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
	/// Shows the preferences file in Explorer. It is plain JSON and a person is allowed to read or
	/// edit it, which is most of the reason it is a file rather than something in the registry.
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
}
