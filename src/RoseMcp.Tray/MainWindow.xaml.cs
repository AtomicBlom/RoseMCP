using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Ui;
using RoseMcp.Ui.Core;

using Windows.ApplicationModel.DataTransfer;

namespace RoseMcp.Tray;

/// <summary>Shows what is loaded, what state it is in, what it is doing, and what it costs.</summary>
public sealed partial class MainWindow : Window
{
	/// <summary>
	/// Quick enough that a progress bar moves and elapsed times tick over, which is the whole
	/// point of watching a load. Only used while something is actually in flight.
	/// </summary>
	private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(400);

	/// <summary>
	/// Slow enough not to be busywork. An idle broker has nothing to say beyond memory, and memory
	/// does not move on its own.
	/// </summary>
	private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);

	/// <summary>Long enough to be seen; short enough that the button is itself again before the next click.</summary>
	private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

	private const string CopyGlyph = "\uE8C8";
	private const string CopiedGlyph = "\uE73E";

	// Logical pixels, scaled to the monitor before use. Room for two or three cards with an
	// operation running in each; the minimum keeps the header's two halves from colliding.
	private const int InitialWidth = 780;
	private const int InitialHeight = 620;
	private const int MinimumWidth = 560;
	private const int MinimumHeight = 400;

	/// <summary>
	/// Rows live here and are updated in place, so the list is only ever added to or removed from.
	/// Rebuilding it each refresh would restart every progress bar and close any open expander.
	/// </summary>
	private readonly ObservableCollection<WorkspaceRow> _rows = [];

	/// <summary>
	/// Session rows, updated in place for the same reason the workspace rows are: a row rebuilt four
	/// times a second never animates its progress bar and closes any expander the reader opened.
	/// </summary>
	private readonly ObservableCollection<SessionRow> _sessionRows = [];

	/// <summary>
	/// A second timer, at one second, for the two labels on a session that move without anything
	/// being polled: how long ago the target last breathed, and how long until it resumes itself.
	/// <para>
	/// Separate from the refresh because it costs nothing and asks nobody anything. Folding it into
	/// the refresh would mean either polling the broker every second when it is idle, or a heartbeat
	/// that steps in two-second jumps -- and the heartbeat is what a reader watches precisely when
	/// they suspect an app has stopped.
	/// </para>
	/// </summary>
	private readonly DispatcherQueueTimer _ticking;

	private readonly DispatcherQueueTimer _timer;
	private readonly App _app = (App)Application.Current;
	private readonly string _endpoint;
	private bool _exiting;

	public MainWindow()
	{
		InitializeComponent();

		_endpoint = $"http://{_app.Options.Host}:{_app.Options.Port}";
		EndpointText.Text = _endpoint;
		RegistrationText.Text = RegistrationCommand(_endpoint);
		Workspaces.ItemsSource = _rows;
		Sessions.ItemsSource = _sessionRows;

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(TitleBarArea);
		ApplyIcon();
		ApplySize();

		AppWindow.Closing += OnClosing;

		ShowCommand = new ShowWindowCommand(this);

		// A crash that was contained still happened, and this window is the only place a person
		// would see it. The log has it either way.
		CrashHandler.Reported += ShowNotice;

		_timer = DispatcherQueue.CreateTimer();
		_timer.Interval = IdleInterval;
		_timer.Tick += (_, _) => Refresh();
		_timer.Start();

		_ticking = DispatcherQueue.CreateTimer();
		_ticking.Interval = TimeSpan.FromSeconds(1);
		_ticking.Tick += (_, _) => Tick();
		_ticking.Start();

		Refresh();
	}

	/// <summary>Bound to the tray icon's left click, which is the usual way back to the window.</summary>
	public ICommand ShowCommand { get; }

	private WorkspaceManager Manager => _app.Services.GetRequiredService<WorkspaceManager>();

	/// <summary>
	/// Puts the shared icon on the window and its art on the marks inside it, then the tray icon,
	/// which is the one part no other window needs.
	/// <para>
	/// The tray asks for the 32-pixel frame rather than the 16: the file has a purpose-drawn image at
	/// each size, so asking for the larger one means Windows only ever scales down, which is far
	/// kinder than scaling up on a high-DPI taskbar.
	/// </para>
	/// </summary>
	private void ApplyIcon()
	{
		var icon = WindowChrome.ApplyIcon(this, TitleMark, EmptyMark);
		if (icon is null) return;

		try
		{
			Tray.Icon = new System.Drawing.Icon(icon, 32, 32);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException)
		{
			Debug.WriteLine($"Could not apply the tray icon: {exception.Message}");
		}
	}

	/// <summary>
	/// WinUI's default window is sized for an application; this is a status panel. Sizes are in
	/// logical pixels and scaled here, because AppWindow works in physical ones.
	/// </summary>
	private void ApplySize() =>
		WindowChrome.ApplySize(this, InitialWidth, InitialHeight, MinimumWidth, MinimumHeight);

	private void Refresh()
	{
		var workspaces = Manager.Describe();
		var sessions = SessionManager.Describe();

		MergeRows(workspaces);
		MergeSessions(sessions);

		var running = workspaces.Sum(summary => summary.Running.Count) + sessions.Sum(summary => summary.Running.Count);
		Headline.Text = DescribeHeadline(workspaces, sessions);
		Subtitle.Text = DescribeSubtitle(workspaces, sessions, running);
		Tray.ToolTipText = DescribeTooltip(workspaces.Count, sessions.Count, running);

		// Empty only when there is neither kind of thing. A machine with a debug session and no
		// loaded solution is not idle, and telling it how to register an endpoint would be answering
		// a question nobody asked.
		var empty = workspaces.Count == 0 && sessions.Count == 0;
		EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
		WorkspaceScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

		var hasSessions = sessions.Count > 0;
		SessionsCaption.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;

		// Poll harder only while there is something to watch. Setting the interval restarts the
		// timer, so only do it when it actually changed.
		var interval = running > 0 ? ActiveInterval : IdleInterval;
		if (_timer.Interval != interval) _timer.Interval = interval;
	}

	/// <summary>The live-app sessions, read from the same manager every other reader uses.</summary>
	private LiveAppSessionManager SessionManager => _app.Services.GetRequiredService<LiveAppSessionManager>();

	/// <summary>Adds rows for new sessions, updates the rest, and drops the ones that ended.</summary>
	private void MergeSessions(IReadOnlyList<LiveAppSessionSummary> summaries) =>
		Rows.Merge(
			_sessionRows,
			summaries,
			row => row.SessionId,
			summary => summary.SessionId,
			summary => new SessionRow(summary),
			(row, summary) => row.Update(summary));

	/// <summary>
	/// Moves the labels that change without anything being asked: a target's heartbeat and the
	/// countdown to it resuming.
	/// </summary>
	private void Tick()
	{
		var now = DateTime.UtcNow;

		foreach (var row in _sessionRows)
		{
			row.Tick(now);
		}
	}

	/// <summary>
	/// Puts a sentence in front of the reader. For the things that happen once and need saying once:
	/// a contained crash, an inspector that is not installed, a detach that did not take.
	/// </summary>
	private void ShowNotice(string message)
	{
		// From a background thread in the crash-handler case, where the exception may have arrived on
		// any thread at all.
		DispatcherQueue.TryEnqueue(() =>
		{
			Notice.Message = message;
			Notice.IsOpen = true;
		});
	}

	private void OnInspectSession(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string sessionId }) return;

		OpenInspector(sessionId);
	}

	private void OnOpenInspector(object sender, RoutedEventArgs e) => OpenInspector(sessionId: null);

	/// <summary>
	/// Starts the inspector against this broker, on one session or on the list.
	/// <para>
	/// A missing inspector is said rather than thrown: it is an optional app, and a tray that died
	/// because a menu item pointed at something not installed would take every warm worker with it.
	/// </para>
	/// </summary>
	private void OpenInspector(string? sessionId)
	{
		try
		{
			InspectorLauncher.Launch(
				_app.Options.Host,
				_app.Options.Port,
				_app.OperatorToken,
				sessionId,
				_app.Options.InspectorPath);
		}
		catch (Exception exception) when (exception is FileNotFoundException or Win32Exception or InvalidOperationException)
		{
			ShowNotice(exception.Message);
		}
	}

	/// <summary>
	/// Copies the command that starts an inspector against this broker, for running one from source
	/// or from a terminal. It carries this run's token, which is why it is a copy rather than
	/// something written down anywhere.
	/// </summary>
	private void OnCopyInspectorCommand(object sender, RoutedEventArgs e)
	{
		var inspector = InspectorLauncher.ResolvePath(_app.Options.InspectorPath);

		if (inspector is null)
		{
			ShowNotice(
				"No inspector is installed, so there is no command to copy. A published install has it in "
					+ "'inspector' beside the tray's own folder; from source, build RoseMcp.Inspector.");

			return;
		}

		Copy(InspectorLauncher.CommandLine(
			inspector,
			_app.Options.Host,
			_app.Options.Port,
			_app.OperatorToken,
			sessionId: null));
	}

	/// <summary>
	/// Ends a session and takes the debugger off its target, leaving the app running.
	/// <para>
	/// Through the operator path rather than the client one, because this window is not an MCP
	/// client: the ownership check would refuse every session an agent started, which is all of them.
	/// </para>
	/// </summary>
	private async void OnDetachSession(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string sessionId }) return;

		var session = SessionManager.ForOperator(sessionId);
		await SessionManager.CloseForOperatorAsync(sessionId, CancellationToken.None);

		// A close whose detach failed leaves an app being watched by a debugger nothing is driving,
		// which is the one outcome here worth interrupting somebody about.
		if (session?.DetachFailure is { Length: > 0 } failure) ShowNotice(failure);

		Refresh();
	}

	/// <summary>
	/// Shows the host log that explains one session. The file when the host named one, else the
	/// folder its logs go in -- which is still the right place to look, just not the right line.
	/// </summary>
	private void OnOpenHostLog(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string sessionId }) return;

		var row = _sessionRows.FirstOrDefault(candidate => candidate.SessionId == sessionId);

		if (row?.HostLogPath is { Length: > 0 } path && File.Exists(path))
		{
			OpenInExplorer($"/select,\"{path}\"");
			return;
		}

		var directory = RoseLogFile.DirectoryFor("LiveApp");
		Directory.CreateDirectory(directory);
		OpenInExplorer($"\"{directory}\"");
	}

	/// <summary>Adds rows for new workspaces, updates the rest, and drops the ones that closed.</summary>
	private void MergeRows(IReadOnlyList<WorkspaceSummary> summaries)
	{
		for (var index = _rows.Count - 1; index >= 0; index--)
		{
			var stillOpen = summaries.Any(summary => Same(summary.Workspace, _rows[index].SolutionPath));
			if (!stillOpen) _rows.RemoveAt(index);
		}

		foreach (var summary in summaries)
		{
			var existing = _rows.FirstOrDefault(row => Same(row.SolutionPath, summary.Workspace));

			if (existing is null)
			{
				_rows.Add(new WorkspaceRow(summary));
				continue;
			}

			existing.Update(summary);
		}
	}

	/// <summary>
	/// The one line to read: how much is loaded, whether it is up yet, and how many targets are
	/// being debugged.
	/// </summary>
	public static string DescribeHeadline(
		IReadOnlyList<WorkspaceSummary> workspaces,
		IReadOnlyList<LiveAppSessionSummary> sessions)
	{
		var debugging = sessions.Count > 0 ? Format.Count(sessions.Count, "session") : null;

		if (workspaces.Count == 0)
		{
			return debugging is null ? "Nothing loaded" : $"Debugging {debugging}";
		}

		var solutions = Format.Count(workspaces.Count, "solution");
		var allLoading = workspaces.All(summary => summary.State == WorkspaceState.Loading);
		var loaded = allLoading ? $"Loading {solutions}" : $"{solutions} loaded";

		return debugging is null ? loaded : $"{loaded}, debugging {debugging}";
	}

	/// <summary>What it costs, whether it is busy, and whether anything below needs a look.</summary>
	public static string DescribeSubtitle(
		IReadOnlyList<WorkspaceSummary> workspaces,
		IReadOnlyList<LiveAppSessionSummary> sessions,
		int running)
	{
		if (workspaces.Count == 0 && sessions.Count == 0) return "Waiting for a client to ask about one.";

		var parts = new List<string>();

		if (workspaces.Count > 0)
		{
			var workingSet = workspaces.Sum(summary => summary.WorkingSetBytes ?? 0);
			parts.Add($"{Format.Bytes(workingSet)} working set");
		}

		parts.Add(running == 0 ? "idle" : $"{Format.Count(running, "operation")} running");

		var troubled = workspaces.Count(summary => summary.State is WorkspaceState.Degraded or WorkspaceState.Faulted);
		if (troubled > 0) parts.Add(troubled == 1 ? "1 needs attention" : $"{troubled} need attention");

		// A held target is the one state here somebody has to end: an app frozen by a debugger stays
		// frozen until its safety timer or a person lets it go.
		var held = sessions.Count(summary => summary.Stop is not null);
		if (held > 0) parts.Add($"{Format.Count(held, "target")} stopped");

		return string.Join(Format.Separator, parts);
	}

	/// <summary>
	/// Kept to a few words: this is read hovering over a 16-pixel icon, and it is the only view of
	/// the broker available without opening the window.
	/// </summary>
	public static string DescribeTooltip(int workspaces, int sessions, int running)
	{
		if (workspaces == 0 && sessions == 0) return "RoseMCP - nothing loaded";

		var parts = new List<string>();

		if (workspaces > 0) parts.Add(Format.Count(workspaces, "solution"));
		if (sessions > 0) parts.Add(Format.Count(sessions, "session"));

		parts.Add(running == 0 ? "idle" : $"{running} running");

		return $"RoseMCP - {string.Join(", ", parts)}";
	}

	/// <summary>How a Claude Code user points their agent at this broker over http.</summary>
	public static string RegistrationCommand(string endpoint) => $"claude mcp add --transport http rose {endpoint}";

	private static bool Same(string left, string right) =>
		string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

	private async void OnReload(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string solutionPath }) return;

		await Manager.RestartAsync(WorkspaceHints.From(solutionPath), CancellationToken.None);
		Refresh();
	}

	private async void OnClose(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string solutionPath }) return;

		await Manager.CloseAsync(WorkspaceHints.From(solutionPath), CancellationToken.None);
		Refresh();
	}

	private void OnOpenFolder(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string solutionPath }) return;

		OpenInExplorer($"/select,\"{solutionPath}\"");
	}

	/// <summary>
	/// The folder above this component's: Server, Worker and Tray sit side by side under it, and
	/// the question that brings someone here is usually which of them has the answer.
	/// </summary>
	private void OnOpenLogs(object sender, RoutedEventArgs e)
	{
		var own = RoseLogFile.DirectoryFor("Tray");
		var root = Path.GetDirectoryName(own) ?? own;

		Directory.CreateDirectory(root);
		OpenInExplorer($"\"{root}\"");
	}

	/// <summary>
	/// Both menus carry the same two items whose text depends on the machine, and Windows owns the
	/// answer to each -- the startup key can be changed by anything, including a second copy of this
	/// app, and the inspector can be installed or not. So both are read when the menu opens rather
	/// than cached.
	/// </summary>
	private void OnMenuOpening(object sender, object e)
	{
		var enabled = StartupRegistration.IsEnabled;
		var elsewhere = StartupRegistration.PointsElsewhere;

		foreach (var item in (ToggleMenuFlyoutItem?[])[TrayStartWithWindows, WindowStartWithWindows])
		{
			if (item is null) continue;

			item.IsChecked = enabled;

			// An install that has moved: Windows still starts the old copy, so saying "off" would be
			// a lie and saying "on" would point at the wrong exe.
			item.Text = elsewhere ? "Start with Windows (registered elsewhere)" : "Start with Windows";
		}

		// An item that opens nothing is worse than one that says why. The inspector is optional, and
		// a source tree without it built is the ordinary case for anyone working on this repository.
		var installed = InspectorLauncher.ResolvePath(_app.Options.InspectorPath) is not null;

		foreach (var item in (MenuFlyoutItem?[])[TrayOpenInspector, WindowOpenInspector])
		{
			if (item is null) continue;

			item.Text = installed ? "Open inspector" : "Open inspector (not installed)";
		}
	}

	private void OnToggleStartWithWindows(object sender, RoutedEventArgs e)
	{
		// Off means off even when the registration belongs to another copy: the checkbox was showing
		// unchecked, so the click asks for on, and on means this executable.
		var wanted = !StartupRegistration.IsEnabled;

		if (StartupRegistration.Set(wanted)) return;

		// A toggle that silently does nothing is worse than one that is not offered, and the menu is
		// the only surface certain to be visible here -- this is reachable from the tray with the
		// window hidden -- so the menu carries the news. Reset when the menu next opens.
		if (sender is ToggleMenuFlyoutItem item)
		{
			item.IsChecked = StartupRegistration.IsEnabled;
			item.Text = "Start with Windows (Windows refused)";
		}
	}

	private void OnCopyEndpoint(object sender, RoutedEventArgs e) => Copy(_endpoint, EndpointCopyGlyph);

	private void OnCopyRegistration(object sender, RoutedEventArgs e) => Copy(RegistrationText.Text, RegistrationCopyGlyph);

	private void OnShow(object sender, RoutedEventArgs e) => Show();

	private async void OnCloseAll(object sender, RoutedEventArgs e)
	{
		foreach (var worker in Manager.Workers)
		{
			await Manager.CloseAsync(WorkspaceHints.From(worker.SolutionPath), CancellationToken.None);
		}

		Refresh();
	}

	private async void OnExit(object sender, RoutedEventArgs e)
	{
		_exiting = true;
		Tray.Dispose();
		await _app.ShutdownAsync();
	}

	/// <summary>
	/// Closing the window hides it. A tray app that quits when its window closes is not a tray app,
	/// and quitting here would take every loaded solution with it; Exit in the menu is the
	/// deliberate way out, and the only one.
	/// </summary>
	private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (_exiting) return;

		args.Cancel = true;
		sender.Hide();
	}

	internal void Show()
	{
		AppWindow.Show();
		Activate();
	}

	/// <summary>
	/// Copies, and says so by turning the button's glyph into a tick for a moment. A clipboard has
	/// no other visible effect, and a button that does nothing visible reads as broken.
	/// </summary>
	private void Copy(string text, FontIcon glyph)
	{
		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);

		glyph.Glyph = CopiedGlyph;

		var revert = DispatcherQueue.CreateTimer();
		revert.Interval = CopiedFor;
		revert.IsRepeating = false;
		revert.Tick += (_, _) => glyph.Glyph = CopyGlyph;
		revert.Start();
	}

	/// <summary>
	/// Copies, and says so in the notice bar.
	/// <para>
	/// The overload above turns a button's glyph into a tick, which is the right feedback for a
	/// button that is still on screen afterwards. A menu item is not: the flyout closes on the click,
	/// so there is nothing left to change, and a clipboard has no other visible effect.
	/// </para>
	/// </summary>
	private void Copy(string text)
	{
		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);

		ShowNotice("Copied to the clipboard. It carries this run's operator token, which changes when the tray restarts.");
	}

	private static void OpenInExplorer(string arguments)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			Debug.WriteLine($"Could not open Explorer: {exception.Message}");
		}
	}

	private sealed class ShowWindowCommand(MainWindow window) : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter) => window.Show();
	}
}
