using System.Collections.ObjectModel;
using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Ui;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector;

/// <summary>
/// The inspector's one window: the sessions the tray knows about on the left, and everything about
/// the chosen one on the right.
/// </summary>
public sealed partial class MainWindow : Window
{
	/// <summary>
	/// How often the session list is re-read. A second is what the tray's own refresh runs at, so
	/// the two windows agree about what exists rather than one lagging the other by a poll.
	/// </summary>
	private static readonly TimeSpan SessionInterval = TimeSpan.FromSeconds(1);

	// Logical pixels. Wide enough for a stack beside a variable tree, which is the widest thing
	// this window will ever have to show; the minimum keeps the session list from squeezing the
	// header's two halves into each other.
	private const int InitialWidth = 1080;
	private const int InitialHeight = 720;
	private const int MinimumWidth = 760;
	private const int MinimumHeight = 480;

	private readonly App _app = (App)Application.Current;
	private readonly ObservableCollection<SessionRow> _sessions = [];
	private readonly Dictionary<string, InspectedSession> _inspected = new(StringComparer.Ordinal);
	private readonly OperatorClient _client;
	private readonly PollLoop _sessionPoll;
	private readonly DispatcherQueueTimer _ticking;

	/// <summary>
	/// The session to show once it appears. A fresh attach is in the tray's list within a second,
	/// but Inspect can arrive before that -- so the id is remembered rather than reported missing.
	/// </summary>
	private string? _wanted;

	private SessionRow? _chosen;

	public MainWindow()
	{
		InitializeComponent();

		_client = new OperatorClient(_app.Options.BaseAddress, _app.Options.Token);
		_wanted = _app.Options.SessionId;

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(TitleBarArea);
		WindowChrome.ApplySize(this, InitialWidth, InitialHeight, MinimumWidth, MinimumHeight);
		WindowChrome.ApplyIcon(this, TitleMark);

		SessionList.ItemsSource = _sessions;
		Events.Attach(_client, Report);
		Breakpoints.Attach(_client, Report);

		_sessionPoll = new PollLoop(RefreshSessionsAsync, SessionInterval, exception => Report(exception));

		// A second timer for the labels that move without anything being asked: how long ago the
		// target last breathed, and how long until it resumes itself. Folding these into the poll
		// would make the heartbeat step in poll-sized jumps, and the heartbeat is exactly what a
		// reader watches when they suspect an app has stopped.
		_ticking = DispatcherQueue.CreateTimer();
		_ticking.Interval = TimeSpan.FromSeconds(1);
		_ticking.Tick += (_, _) => Tick();
		_ticking.Start();

		if (_app.OptionsProblem is { } problem) ShowNotice(problem, InfoBarSeverity.Warning);

		ShowEmpty(InspectorText.NoSessions, string.Empty);
		_sessionPoll.Start();

		// Everything this window polls stops with it. A loop left running against a closed window
		// keeps asking the tray questions nobody will read, and holds the process alive to do it.
		Closed += (_, _) =>
		{
			_ticking.Stop();
			_sessionPoll.Stop();
			Events.Showing(false);
			Breakpoints.Showing(false);
			_client.Dispose();
		};
	}

	/// <summary>Shows a session by id, or remembers it until the tray lists it.</summary>
	public void ShowSession(string? sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId)) return;

		_wanted = sessionId;

		var row = _sessions.FirstOrDefault(session => session.SessionId == sessionId);
		if (row is null)
		{
			ShowEmpty(InspectorText.WaitingForSession(sessionId), string.Empty);
			return;
		}

		SessionList.SelectedItem = row;
	}

	/// <summary>Brings this window forward, which is what a second Inspect click is asking for.</summary>
	public void BringToFront()
	{
		AppWindow.Show();
		Activate();
	}

	/// <summary>
	/// Reads the session list and keeps the rows alive across the refresh, so the row a reader is
	/// about to click does not move under them.
	/// </summary>
	private async Task RefreshSessionsAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<LiveAppSessionSummary> summaries;

		try
		{
			summaries = await _client.SessionsAsync(cancellationToken);
		}
		catch (OperatorException failure)
		{
			Offline(failure);
			return;
		}

		Rows.Merge(
			_sessions,
			summaries,
			row => row.SessionId,
			summary => summary.SessionId,
			summary => new SessionRow(summary),
			(row, summary) => row.Update(summary));

		// A pending id becomes a selection the moment the tray lists it, which is what makes
		// Inspect work on a session that was attached a heartbeat ago.
		if (_wanted is { } pending && _chosen?.SessionId != pending)
		{
			var arrived = _sessions.FirstOrDefault(session => session.SessionId == pending);
			if (arrived is not null) SessionList.SelectedItem = arrived;
		}

		if (_sessions.Count == 0)
		{
			_chosen = null;
			ShowEmpty(InspectorText.NoSessions, string.Empty);
			return;
		}

		// Nothing chosen and something to choose: take the first, because a window that opens onto
		// a list and shows nothing has made the reader click to see what it already knows.
		if (_chosen is null && _wanted is null) SessionList.SelectedItem = _sessions[0];

		if (_chosen is { } current && !_sessions.Contains(current))
		{
			ShowEmpty(InspectorText.SessionGone(current.SessionId), string.Empty);
			_chosen = null;
		}

		if (_chosen is not null) ShowHeader(_chosen);
	}

	private void OnSessionChosen(object sender, SelectionChangedEventArgs args)
	{
		if (SessionList.SelectedItem is not SessionRow row) return;

		_chosen = row;
		_wanted = null;

		var inspected = _inspected.TryGetValue(row.SessionId, out var existing)
			? existing
			: _inspected[row.SessionId] = new InspectedSession(row.SessionId);

		Events.Bind(inspected);
		Breakpoints.Bind(inspected);

		EmptyState.Visibility = Visibility.Collapsed;
		DetachButton.IsEnabled = true;
		OpenLogButton.IsEnabled = row.HostLogPath is not null;

		ShowHeader(row);
		ShowTab();
	}

	private void OnTabChosen(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowTab();

	/// <summary>
	/// Shows the chosen pane and hides the rest, and tells each whether it is the visible one --
	/// a pane that cannot see the screen must not be polling on the reader's behalf.
	/// </summary>
	private void ShowTab()
	{
		var events = Tabs.SelectedItem == EventsTab;

		Events.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
		Breakpoints.Visibility = events ? Visibility.Collapsed : Visibility.Visible;

		Events.Showing(events && _chosen is not null);
		Breakpoints.Showing(!events && _chosen is not null);
	}

	private void ShowHeader(SessionRow row)
	{
		SessionName.Text = row.DisplayName;
		StateText.Text = row.StateLabel;
		FactsText.Text = row.Facts;
		XamlFactText.Text = row.XamlFact;

		ExecutionPill.Visibility = row.IsStopped ? Visibility.Visible : Visibility.Collapsed;
		ExecutionText.Text = row.IsHeld ? $"{row.ExecutionLabel} · {row.ResumeLabel}" : row.ExecutionLabel;
		OpenLogButton.IsEnabled = row.HostLogPath is not null;
	}

	private void Tick()
	{
		var now = DateTime.UtcNow;

		foreach (var row in _sessions)
		{
			row.Tick(now);
		}

		if (_chosen is { } chosen && _sessions.Contains(chosen)) ShowHeader(chosen);
	}

	/// <summary>
	/// What to show when the tray is not answering. The list is left alone rather than emptied: a
	/// tray that restarts comes back in a second, and clearing the window each time would make a
	/// blink of unreachability look like every session ending.
	/// </summary>
	private void Offline(OperatorException failure)
	{
		var detail = failure.Failure switch
		{
			OperatorFailure.Unauthorized when !_client.HasToken => InspectorText.NoToken,
			OperatorFailure.Unauthorized => InspectorText.TokenRefused,
			_ => failure.Message,
		};

		ShowEmpty("Not connected", detail);
	}

	private void ShowEmpty(string title, string detail)
	{
		EmptyTitle.Text = title;
		EmptyDetail.Text = detail;
		EmptyState.Visibility = Visibility.Visible;
		Events.Visibility = Visibility.Collapsed;
		Breakpoints.Visibility = Visibility.Collapsed;
		Events.Showing(false);
		Breakpoints.Showing(false);
		DetachButton.IsEnabled = false;
		OpenLogButton.IsEnabled = false;
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
		if (_chosen is not { } row) return;

		Busy.IsActive = true;

		try
		{
			var closed = await _client.DetachAsync(row.SessionId, CancellationToken.None);

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
	/// Opens the host's own log, selected in Explorer where there is one and the folder otherwise.
	/// The log is where the answer is when a session has gone wrong in a way the window cannot show.
	/// </summary>
	private void OnOpenHostLog(object sender, RoutedEventArgs args)
	{
		try
		{
			var log = _chosen?.HostLogPath;

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
}
