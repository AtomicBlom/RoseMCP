using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Settings;
using RoseMcp.Ui;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector;

/// <summary>
/// The inspector's one window, which is about one debugged process.
/// <para>
/// One per process rather than one per machine, so the taskbar tells two debugged apps apart and
/// the title says which is which. There is deliberately no list of other sessions here: the tray
/// is where sessions are listed, and a window that could be re-pointed at another process would
/// make its own title a lie.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
	/// <summary>
	/// How often the session is re-read. A second is what the tray's own refresh runs at, so the
	/// two windows agree about what exists rather than one lagging the other by a poll.
	/// </summary>
	private static readonly TimeSpan SessionInterval = TimeSpan.FromSeconds(1);

	// Logical pixels. Wide enough for a stack beside a variable tree, which is the widest thing
	// this window will have to show; the minimum keeps the header's two halves from colliding.
	private const int InitialWidth = 980;
	private const int InitialHeight = 720;
	private const int MinimumWidth = 640;
	private const int MinimumHeight = 480;

	private readonly App _app = (App)Application.Current;
	private readonly OperatorClient _client;
	private readonly PollLoop _sessionPoll;
	private readonly DispatcherQueueTimer _ticking;

	private InspectedSession? _inspected;
	private SessionRow? _row;

	/// <summary>
	/// The session this window is about, once it has one. Set from the command line, from a
	/// redirected launch, or by adopting the only session there is.
	/// </summary>
	private string? _sessionId;

	public MainWindow()
	{
		InitializeComponent();

		_client = new OperatorClient(_app.Options.BaseAddress, _app.Options.Token);
		_sessionId = _app.Options.SessionId;

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(TitleBarArea);
		WindowChrome.ApplySize(this, InitialWidth, InitialHeight, MinimumWidth, MinimumHeight);
		WindowChrome.ApplyIcon(this, TitleMark);

		Events.Attach(_client, Report);
		Breakpoints.Attach(_client, Report);

		_sessionPoll = new PollLoop(RefreshAsync, SessionInterval, Report);

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

	/// <summary>
	/// Points this window at a session, which a redirected launch does. Only ever the same process
	/// this window is already about, because the single-instance key is that process.
	/// </summary>
	public void ShowSession(string? sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId) || sessionId == _sessionId) return;

		_sessionId = sessionId;
		_inspected = null;
		_row = null;
		_sessionPoll.Kick();
	}

	/// <summary>Brings this window forward, which is what a second Inspect click is asking for.</summary>
	public void BringToFront()
	{
		AppWindow.Show();
		Activate();
	}

	/// <summary>
	/// Reads the session this window is about, adopting one when it was not told which.
	/// <para>
	/// Adopting is only ever from one candidate. Several is an ambiguity this window cannot settle
	/// -- it has no idea which process the reader meant -- so it names them and waits, rather than
	/// picking one and being confidently about the wrong program.
	/// </para>
	/// </summary>
	private async Task RefreshAsync(CancellationToken cancellationToken)
	{
		try
		{
			if (_sessionId is null && !await AdoptAsync(cancellationToken)) return;

			Show(await _client.SessionAsync(_sessionId!, cancellationToken));
		}
		catch (OperatorException failure) when (failure.Failure == OperatorFailure.NotFound)
		{
			var gone = _sessionId;
			_sessionId = null;
			_inspected = null;
			_row = null;

			ShowEmpty(InspectorText.SessionGone(gone ?? "?"), string.Empty);
		}
		catch (OperatorException failure)
		{
			Offline(failure);
		}
	}

	/// <summary>Whether a session to be about could be settled on. False leaves the empty state up.</summary>
	private async Task<bool> AdoptAsync(CancellationToken cancellationToken)
	{
		var sessions = await _client.SessionsAsync(cancellationToken);

		if (sessions.Count == 0)
		{
			ShowEmpty(InspectorText.NoSessions, string.Empty);
			return false;
		}

		if (sessions.Count > 1)
		{
			ShowEmpty(
				"Several targets are being debugged",
				"This window is about one process, and nothing here says which one you meant. Open the "
					+ "inspector from a session in the tray. Right now: "
					+ string.Join(", ", sessions.Select(session => session.TargetDescription)) + ".");

			return false;
		}

		_sessionId = sessions[0].SessionId;
		return true;
	}

	/// <summary>Binds the panes to the session, the first time it is seen, and renders the header.</summary>
	private void Show(LiveAppSessionSummary summary)
	{
		if (_row is null || _row.SessionId != summary.SessionId)
		{
			_row = new SessionRow(summary);
			_inspected = new InspectedSession(summary.SessionId);

			Events.Bind(_inspected);
			Breakpoints.Bind(_inspected);

			EmptyState.Visibility = Visibility.Collapsed;
			DetachButton.IsEnabled = true;
			ShowTab();
		}
		else
		{
			_row.Update(summary);
		}

		ShowHeader(_row);
	}

	private void OnTabChosen(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowTab();

	/// <summary>
	/// Shows the chosen pane and hides the rest, and tells each whether it is the visible one --
	/// a pane that cannot see the screen must not be polling on the reader's behalf.
	/// </summary>
	private void ShowTab()
	{
		var events = Tabs.SelectedItem == EventsTab;
		var bound = _inspected is not null;

		Events.Visibility = events && bound ? Visibility.Visible : Visibility.Collapsed;
		Breakpoints.Visibility = !events && bound ? Visibility.Visible : Visibility.Collapsed;

		Events.Showing(events && bound);
		Breakpoints.Showing(!events && bound);
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

		// The window's own name, and the taskbar's. One inspector per debugged process only helps
		// somebody pick between two of them if each says which process it is.
		TitleText.Text = $"{row.DisplayName} — RoseMCP Inspector";
		Title = TitleText.Text;
	}

	private void Tick()
	{
		if (_row is not { } row) return;

		row.Tick(DateTime.UtcNow);
		ShowHeader(row);
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

		SessionName.Text = "No session";
		StateText.Text = string.Empty;
		FactsText.Text = string.Empty;
		XamlFactText.Text = string.Empty;
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
