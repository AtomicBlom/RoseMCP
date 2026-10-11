using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Settings;
using RoseMcp.Ui;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

using Windows.ApplicationModel.DataTransfer;

namespace RoseMcp.App;

/// <summary>
/// The RoseMCP window, organised by agent: Agents, Activity, Workspaces and Debugging, with which build Rose is
/// under the view list and a memory bar along the bottom.
/// <para>
/// It reads the broker through one <see cref="RoseFeed"/>, and only while it can be seen: minimised or closed, every
/// long poll it holds is cancelled, so a window left open in the background holds nothing open on the broker. Rose
/// restarting shows as "not serving" with the reason and comes back by itself; a restart's new token arrives with
/// the next launch from the tray, which the app hands to <see cref="Reconnect"/>.
/// </para>
/// </summary>
public sealed partial class RoseWindow : Window
{
	// Logical pixels. Wide enough for the Workspaces table's nine columns; the minimum keeps a card's operations
	// on one line each.
	private const int InitialWidth = 1240;
	private const int InitialHeight = 800;
	private const int MinimumWidth = 820;
	private const int MinimumHeight = 520;

	private readonly App _app = (App)Application.Current;
	private readonly RoseFeed _feed;
	private readonly DispatcherQueueTimer _ticking;
	private readonly BuildIdentity _own = BuildIdentity.Of(typeof(RoseWindow).Assembly);
	private bool _running;
	private bool _closed;

	public RoseWindow()
	{
		InitializeComponent();

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(TitleBarArea);
		WindowChrome.ApplySize(this, InitialWidth, InitialHeight, MinimumWidth, MinimumHeight);
		WindowChrome.ApplyIcon(this, "rose-mcp.ico", "rose-mcp.png", TitleMark);

		_feed = new RoseFeed(_app.Client);
		_feed.Changed += ShowCounts;
		_feed.MemoryChanged += ShowMemory;
		_feed.ConnectionChanged += ShowConnection;

		Agents.Attach(_feed, this);
		Activity.Attach(_feed, _app.Client);
		Workspaces.Attach(_feed, _app.Client);
		Debugging.Attach(_feed, this, _app.Client);

		// The labels that move on their own: running durations, idle times, close countdowns, report ages.
		_ticking = DispatcherQueue.CreateTimer();
		_ticking.Interval = TimeSpan.FromSeconds(1);
		_ticking.Tick += (_, _) => Tick();

		BuildText.Text = BuildLine.Unknown.Build;
		if (_app.OptionsProblem is { } problem) Say(problem, InfoBarSeverity.Warning);

		// Minimised is not on screen: the feed stops, and starts again, reading at once, when the window comes back.
		AppWindow.Changed += (_, change) =>
		{
			if (change.DidPresenterChange || change.DidVisibilityChange || change.DidSizeChange) Run();
		};

		Closed += (_, _) =>
		{
			_closed = true;
			Run();
		};

		Run();
	}

	/// <summary>Brings this window forward, which is what a second click on the tray icon asks for.</summary>
	public void BringToFront()
	{
		if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();

		AppWindow.Show();
		Activate();
	}

	/// <summary>Reads everything again at once, under the token the app has just been handed.</summary>
	public void Reconnect()
	{
		_feed.Kick();
		Activity.Kick();
	}

	/// <summary>Shows the Workspaces view with a solution's row open.</summary>
	public void ShowWorkspace(string solutionPath)
	{
		Nav.SelectedItem = WorkspacesItem;
		Workspaces.Open(solutionPath);
	}

	/// <summary>Shows the Activity view narrowed to one agent's calls.</summary>
	public void ShowActivityOf(string key, string label)
	{
		Nav.SelectedItem = ActivityItem;
		Activity.ShowAgent(key, label);
	}

	/// <summary>Opens a session's Inspector window.</summary>
	public void OpenSession(string sessionId, int? targetProcessId = null) => _app.ShowInspector(sessionId, targetProcessId);

	/// <summary>Opens an Inspector window on its start page, to attach to a running app or launch one.</summary>
	public void DebugAnApp() => _app.ShowInspector(sessionId: null, targetProcessId: null);

	/// <summary>Runs the feed while the window can be seen and stops it otherwise.</summary>
	private void Run()
	{
		var minimised = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
		var wanted = !_closed && AppWindow.IsVisible && !minimised;
		if (wanted == _running) return;

		_running = wanted;

		if (wanted)
		{
			_feed.Start();
			_ticking.Start();
		}
		else
		{
			_feed.Stop();
			_ticking.Stop();
		}

		ShowView();
	}

	private void OnViewChosen(NavigationView sender, NavigationViewSelectionChangedEventArgs args) => ShowView();

	/// <summary>Shows the chosen view and tells each whether it is the one on screen, so only that one does any work.</summary>
	private void ShowView()
	{
		var selected = Nav.SelectedItem as NavigationViewItem;
		var agents = _running && selected == AgentsItem;
		var activity = _running && selected == ActivityItem;
		var workspaces = _running && selected == WorkspacesItem;
		var debugging = _running && selected == DebuggingItem;

		Agents.Visibility = selected == AgentsItem ? Visibility.Visible : Visibility.Collapsed;
		Activity.Visibility = selected == ActivityItem ? Visibility.Visible : Visibility.Collapsed;
		Workspaces.Visibility = selected == WorkspacesItem ? Visibility.Visible : Visibility.Collapsed;
		Debugging.Visibility = selected == DebuggingItem ? Visibility.Visible : Visibility.Collapsed;

		Agents.Showing(agents);
		Activity.Showing(activity);
		Workspaces.Showing(workspaces);
		Debugging.Showing(debugging);
	}

	private void Tick()
	{
		var now = DateTimeOffset.UtcNow;

		Agents.Tick(now);
		Activity.Tick(now);
		Workspaces.Tick(now.UtcDateTime);
		Debugging.Tick(now.UtcDateTime);
	}

	/// <summary>The counts in the view list, which are on screen whichever view is open.</summary>
	private void ShowCounts()
	{
		Badge(AgentsBadge, _feed.Clients.Clients.Count(client => client.Watching != false));
		Badge(WorkspacesBadge, _feed.Workspaces.Workspaces.Count(workspace => workspace.Summary.Alive));
		Badge(DebuggingBadge, _feed.Sessions.Sessions.Count(session => session.State != LiveAppSessionState.Ended));
	}

	private static void Badge(InfoBadge badge, int count)
	{
		badge.Value = count;
		badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ShowMemory()
	{
		if (_feed.Memory is { } memory) Memory.Show(MemorySummary.Of(memory));
	}

	/// <summary>Whether Rose is serving, why not where it is not, and which build it is.</summary>
	private void ShowConnection()
	{
		if (_feed.Problem is { } problem) Say(problem, InfoBarSeverity.Warning);
		else if (Notice.Severity == InfoBarSeverity.Warning && Notice.Title == NotServing) Notice.IsOpen = false;

		if (_feed.Hello is not { } hello) return;

		var line = BuildLine.Of(hello, _own, DateTimeOffset.UtcNow);
		BuildText.Text = line.Build;
		CheckoutText.Text = line.Checkout;
		CheckoutText.Visibility = line.HasCheckout ? Visibility.Visible : Visibility.Collapsed;
		MismatchText.Text = line.Mismatch;
		MismatchText.Visibility = line.HasMismatch ? Visibility.Visible : Visibility.Collapsed;
	}

	private const string NotServing = "Rose is not answering";

	private void Say(string message, InfoBarSeverity severity)
	{
		Notice.Title = severity == InfoBarSeverity.Warning && _feed is { IsServing: false } ? NotServing : string.Empty;
		Notice.Severity = severity;
		Notice.Message = message;
		Notice.IsOpen = true;
	}

	/// <summary>Reads both preferences on opening rather than holding them, because other windows and the tray write the file too.</summary>
	private void OnSettingsOpening(object sender, object args)
	{
		var settings = RoseSettingsFile.Read();
		ShowOnAttach.IsChecked = settings.ShowInspectorOnAttach;
		ReloadWhenIdle.IsChecked = settings.ReloadRebuiltAnalyzersWhenIdle;
	}

	private void OnToggleShowOnAttach(object sender, RoutedEventArgs args)
	{
		var wanted = sender is ToggleMenuFlyoutItem { IsChecked: true };
		Write(RoseSettingsFile.Read() with { ShowInspectorOnAttach = wanted });
	}

	private void OnToggleReloadWhenIdle(object sender, RoutedEventArgs args)
	{
		var wanted = sender is ToggleMenuFlyoutItem { IsChecked: true };
		Write(RoseSettingsFile.Read() with { ReloadRebuiltAnalyzersWhenIdle = wanted });
	}

	/// <summary>
	/// Writes the settings file read-modify-write, from a read made just now, so the fields other windows wrote are
	/// kept; and says so where the write did not land, rather than leaving a toggle claiming a preference nothing reads.
	/// </summary>
	private void Write(RoseSettings settings)
	{
		if (!RoseSettingsFile.Write(settings))
		{
			Say($"Could not write {RoseSettingsFile.PathFor()}, so that preference is unchanged.", InfoBarSeverity.Warning);
		}
	}

	private async void OnCloseEveryWorkspace(object sender, RoutedEventArgs args)
	{
		var open = _feed.Workspaces.Workspaces.Select(workspace => workspace.Summary.Workspace).OfType<string>().ToList();
		if (open.Count == 0)
		{
			Say("No workspace is open.", InfoBarSeverity.Informational);
			return;
		}

		var dialog = new ContentDialog
		{
			Title = "Close every workspace?",
			Content = new TextBlock
			{
				Text = $"{open.Count} worker(s) stop and give their memory back. Every agent using one loses it, and its next call loads it again from scratch.",
				TextWrapping = TextWrapping.Wrap,
			},
			PrimaryButtonText = "Close them all",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = Content.XamlRoot,
		};

		if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

		var failures = new List<string>();
		foreach (var workspace in open)
		{
			try
			{
				await _app.Client.CloseWorkspaceAsync(workspace, CancellationToken.None);
			}
			catch (OperatorException exception)
			{
				failures.Add($"{Path.GetFileName(workspace)}: {exception.Message}");
			}
		}

		_feed.Kick();
		if (failures.Count > 0) Say($"Not every workspace closed. {string.Join(" ", failures)}", InfoBarSeverity.Error);
	}

	/// <summary>
	/// Copies the command that opens this window against this broker: what somebody keeps in a shortcut, or runs to
	/// open RoseMCP where the tray's icon is out of reach. Valid until the broker restarts, whose new run has a new token.
	/// </summary>
	private void OnCopyCommand(object sender, RoutedEventArgs args)
	{
		var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RoseMcp.App.exe");

		var package = new DataPackage();
		package.SetText(_app.Options.CommandLine(executable));
		Clipboard.SetContent(package);

		Say("Copied. The command carries this run's token, so it works until Rose restarts.", InfoBarSeverity.Success);
	}

	/// <summary>Shows the preferences file in Explorer: plain JSON a person may read or edit.</summary>
	private void OnOpenSettingsFile(object sender, RoutedEventArgs args)
	{
		try
		{
			var path = RoseSettingsFile.PathFor();

			// Written on demand, so "open the settings file" is never an item that opens nothing.
			if (!File.Exists(path)) RoseSettingsFile.Write(RoseSettingsFile.Read());

			using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			Say($"Could not open the settings file: {exception.Message}", InfoBarSeverity.Warning);
		}
	}
}
