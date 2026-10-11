using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.App.Views;
using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.App.Inspector.Panes;

/// <summary>
/// The start page of an Inspector window opened on its own: the running .NET apps with a window to attach to, each
/// that is being debugged opening its session instead; a launch form for an installed app or a program with
/// arguments; and the targets debugged lately. Whatever starts here is the person's session, handed to the window to
/// be about.
/// <para>
/// It reads the running apps on a timer while it is on screen and the installed apps and recent targets each time
/// it is shown, and stops with the window. A start in flight is cancelled when the window goes or the person gives
/// up on it, and the broker then ends a session it started rather than leave one nobody holds.
/// </para>
/// </summary>
public sealed partial class StartPane : UserControl
{
	/// <summary>How often the running apps are read again: often enough that an app just started is there to attach to by the time anyone looks.</summary>
	private static readonly TimeSpan AppsInterval = TimeSpan.FromSeconds(3);

	/// <summary>The most installed apps suggested at once, so a box with a few hundred packages behind it stays a list somebody can read.</summary>
	private const int MaxSuggestions = 30;

	private readonly ObservableCollection<RunningAppRow> _apps = [];
	private readonly ObservableCollection<RecentTargetChip> _recent = [];

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private Action<DebugStart>? _opened;
	private PollLoop? _poll;
	private CancellationTokenSource? _starting;
	private CancellationTokenSource? _reading;

	private IReadOnlyList<RunningApp> _running = [];
	private IReadOnlyList<InstalledApp> _installed = [];
	private InstalledApp? _chosenPackage;
	private bool _every;

	public StartPane()
	{
		InitializeComponent();

		Apps.ItemsSource = _apps;
		Recent.ItemsSource = _recent;
		SharingCallout.Text = StartPage.Sharing;
	}

	/// <summary>
	/// Gives the page the client it reads through, somewhere to report a failure, and what to do with a session once
	/// one is started or opened.
	/// </summary>
	public void Attach(OperatorClient client, Action<Exception> report, Action<DebugStart> opened)
	{
		_client = client;
		_report = report;
		_opened = opened;
		_poll = new PollLoop(ReadAppsAsync, AppsInterval, exception => _report?.Invoke(exception));
	}

	/// <summary>
	/// Whether the page is on screen. Shown, it reads the installed apps and the recent targets and starts reading
	/// the running ones; hidden, it stops, cancelling what it was reading and any start in flight.
	/// </summary>
	public void Showing(bool visible)
	{
		if (!visible)
		{
			_poll?.Stop();
			Cancel(ref _reading);
			Cancel(ref _starting);
			ShowStarting(null);
			return;
		}

		_poll?.Start();
		Cancel(ref _reading);
		_reading = new CancellationTokenSource();
		_ = ReadInstalledAndRecentAsync(_reading.Token);
	}

	private async Task ReadAppsAsync(CancellationToken cancellationToken)
	{
		if (_client is not { } client) return;

		var list = await client.RunningAppsAsync(_every, cancellationToken);
		_running = list.Apps;

		ShowApps(list.Detail);
	}

	/// <summary>Reads the installed apps and what was debugged lately, each saying for itself when it fails.</summary>
	private async Task ReadInstalledAndRecentAsync(CancellationToken cancellationToken)
	{
		if (_client is not { } client) return;

		try
		{
			var recent = await client.RecentTargetsAsync(cancellationToken);
			var now = DateTime.UtcNow;

			Rows.Merge(
				_recent,
				recent.Targets.Select(target => new RecentTargetChip(target, StartPage.Chip(target, now))).ToList(),
				chip => chip.Label,
				chip => chip.Label,
				chip => chip,
				(_, _) => { });

			RecentSection.Visibility = _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

			var installed = await client.InstalledAppsAsync(cancellationToken);
			_installed = installed.Apps;
			Package.PlaceholderText = installed.Detail ?? (installed.Apps.Count == 0 ? "No packaged apps are installed for you" : "Find an installed app");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The page was hidden; nobody is left to show it to.
		}
		catch (OperatorException exception)
		{
			_report?.Invoke(exception);
		}
	}

	private void ShowApps(string? detail)
	{
		var search = Search.Text;
		var shown = _running.Where(app => RunningAppRow.Matches(app, search)).ToList();

		Rows.Merge(
			_apps,
			shown,
			row => row.ProcessId,
			app => app.ProcessId,
			app => new RunningAppRow(app),
			(row, app) => row.Update(app));

		AppsHeader.Text = _every ? "Every .NET process" : "Running apps with a window";
		AppsCaption.Text = detail ?? StartPage.AppsCaption(_every, _running.Count);
		EveryToggle.Content = _every ? "Show only apps with a window" : "Show every .NET process";

		var nothingShown = _apps.Count == 0;
		AppsEmpty.Visibility = nothingShown ? Visibility.Visible : Visibility.Collapsed;
		AppsEmpty.Text = _running.Count == 0
			? "No .NET app with a window is running. Start one, or launch it from here."
			: $"Nothing running matches \"{search}\".";
	}

	private void OnSearchChanged(object sender, TextChangedEventArgs args) => ShowApps(detail: null);

	private void OnToggleEvery(object sender, RoutedEventArgs args)
	{
		_every = !_every;
		_poll?.Kick();
	}

	/// <summary>Opens the session already debugging an app, or attaches to it.</summary>
	private void OnAppAction(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<RunningAppRow>(sender) is not { } row || _client is not { } client) return;

		_ = StartAsync($"Attaching to {row.DisplayName}…", cancellationToken => client.AttachAsync(row.ProcessId, cancellationToken));
	}

	private void OnPackageTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
	{
		if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

		var typed = sender.Text.Trim();
		_chosenPackage = null;
		LaunchPackageButton.IsEnabled = false;

		sender.ItemsSource = _installed
			.Where(app => typed.Length == 0 || StartPage.Describe(app).Contains(typed, StringComparison.OrdinalIgnoreCase))
			.Take(MaxSuggestions)
			.Select(StartPage.Describe)
			.ToList();
	}

	private void OnPackageChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
	{
		var chosen = args.SelectedItem as string;
		_chosenPackage = _installed.FirstOrDefault(app => StartPage.Describe(app) == chosen);
		LaunchPackageButton.IsEnabled = _chosenPackage is not null;
	}

	private void OnLaunchPackage(object sender, RoutedEventArgs args)
	{
		if (_chosenPackage is not { } app || _client is not { } client) return;

		var request = StartPage.Package(app);
		_ = StartAsync($"Launching {app.DisplayName}…", cancellationToken => client.LaunchAsync(request, cancellationToken));
	}

	private void OnLaunchProgram(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client) return;

		if (StartPage.Program(ProgramPath.Text, ProgramArguments.Text, out var problem) is not { } request)
		{
			_report?.Invoke(new OperatorException(OperatorFailure.Refused, problem ?? "That cannot be launched.", status: null));
			return;
		}

		var name = Path.GetFileName(request.ExecutablePath);
		_ = StartAsync($"Launching {name}…", cancellationToken => client.LaunchAsync(request, cancellationToken));
	}

	/// <summary>Does a recent target again: launches it as it was launched, or attaches to the one running copy of it.</summary>
	private void OnRecent(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<RecentTargetChip>(sender) is not { } chip || _client is not { } client) return;

		if (StartPage.Again(chip.Target, _running, out var problem) is not { } again)
		{
			_report?.Invoke(new OperatorException(OperatorFailure.Refused, problem ?? "That cannot be debugged again.", status: null));
			return;
		}

		if (again.ProcessId is { } processId)
		{
			_ = StartAsync($"Attaching to {chip.Target.DisplayName}…", cancellationToken => client.AttachAsync(processId, cancellationToken));
			return;
		}

		_ = StartAsync($"Launching {chip.Target.DisplayName}…", cancellationToken => client.LaunchAsync(again.Launch!, cancellationToken));
	}

	private void OnCancelStart(object sender, RoutedEventArgs args)
	{
		Cancel(ref _starting);
		ShowStarting(null);
	}

	/// <summary>
	/// One start, with the page saying what is starting until it answers. One at a time: a second click while one is in
	/// flight does nothing, since two starts would be two hosts for whatever the person meant.
	/// </summary>
	private async Task StartAsync(string what, Func<CancellationToken, Task<DebugStart>> start)
	{
		if (_starting is not null) return;

		var starting = new CancellationTokenSource();
		_starting = starting;
		ShowStarting(what);

		try
		{
			var started = await start(starting.Token);
			_opened?.Invoke(started);
		}
		catch (OperationCanceledException) when (starting.IsCancellationRequested)
		{
			// Given up on, or the window went; the broker ends a session the start made.
		}
		catch (OperatorException exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			if (ReferenceEquals(_starting, starting))
			{
				_starting = null;
				ShowStarting(null);
			}

			starting.Dispose();
		}
	}

	private void ShowStarting(string? what)
	{
		StartingBar.Visibility = what is null ? Visibility.Collapsed : Visibility.Visible;
		StartingText.Text = what ?? string.Empty;
	}

	private static void Cancel(ref CancellationTokenSource? source)
	{
		var cancelled = source;
		source = null;

		if (cancelled is null) return;

		try
		{
			cancelled.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The start finished and let it go as this cancelled it.
		}
	}
}
