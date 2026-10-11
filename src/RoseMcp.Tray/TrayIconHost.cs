using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

using H.NotifyIcon;
using H.NotifyIcon.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Settings;
using RoseMcp.Ui;
using RoseMcp.Ui.Core.Tray;
using RoseMcp.Ui.Core.Updates;

namespace RoseMcp.Tray;

/// <summary>
/// The tray process's whole user interface: the notification icon, its one menu, and the notifications it
/// raises. There is no window; opening RoseMCP starts the RoseMCP app in a process of its own.
/// <para>
/// No window, because a window crash must not take the broker and every agent's workspace with it, and a
/// tray that opened a window at every start, sign-in and deploy was interrupting somebody each time. See
/// <see href="../../docs/decisions/the-tray-process-owns-only-its-icon.md">the decision</see>.
/// </para>
/// <para>
/// Everything here runs on the UI thread: the timers are dispatcher timers, the update check resumes on
/// it, and a failure reported from any other thread is queued onto it. That is what lets this run's
/// notify-once state -- the release it announced, the failures already said, the restart report -- go
/// unlocked. What is remembered across runs is shared with any other tray on the machine, so it is changed
/// only through <see cref="TrayMemoryFile"/>, which reads the file fresh under a lock for each change, and
/// off this thread, since that lock can wait.
/// </para>
/// <para>
/// Lifetime: <see cref="Stopping"/> stops every timer and lets go of the broker's services before the broker
/// stops, so nothing here reads a container that is being torn down; <see cref="Dispose"/> then removes the
/// icon and unhooks the crash handler. An async read still in flight checks <see cref="_stopped"/> when it
/// resumes, and a read of a reader the broker has disposed is caught.
/// </para>
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
	/// <summary>How often the counts and the icon's state are read. Cheap reads of the broker's own tables.</summary>
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	/// <summary>
	/// How often the checkout is asked how far it has moved, which starts git. The reader keeps an answer this
	/// long anyway, so asking more often would only be served the same one.
	/// </summary>
	private static readonly TimeSpan CheckoutInterval = CheckoutDistanceReader.Freshness;

	/// <summary>
	/// How long a second click on the icon is taken as part of the first. A double click arrives as two
	/// clicks, and opening the app twice for it would start a second process only to hand over and exit.
	/// </summary>
	private static readonly TimeSpan ClickSettle = TimeSpan.FromSeconds(1.5);

	/// <summary>
	/// How often an icon Windows would not add is tried again. Short enough that an icon refused at sign-in shows
	/// moments after the taskbar does; each try is one shell call.
	/// </summary>
	private static readonly TimeSpan IconRetryInterval = TimeSpan.FromSeconds(5);

	/// <summary>The install this tray runs from, which keys the build it remembers running last.</summary>
	private static readonly string Install = Environment.ProcessPath ?? "RoseMcp.Tray";

	/// <summary>Tries the icon again while Windows will not add it.</summary>
	private readonly DispatcherQueueTimer _iconRetry;

	/// <summary>
	/// This build, named on the icon. The tray's own assembly rather than the broker's, because the tray is
	/// what a person launched; they ship together, and the broker's children are compared against the broker
	/// by the handshake.
	/// </summary>
	private static readonly BuildIdentity Build = BuildIdentity.Of(typeof(TrayIconHost).Assembly);

	private readonly App _app;
	private readonly DispatcherQueue _queue;
	private readonly TaskbarIcon _icon;
	private readonly MenuFlyout _menu = new();
	private readonly TrayIconArt _art;
	private readonly DispatcherQueueTimer _poll;
	private readonly DispatcherQueueTimer _checkoutTimer;
	private readonly DispatcherQueueTimer? _updateTimer;
	private readonly IDisposable _clicks;

	/// <summary>Held for as long as this copy runs, so a second copy of the same executable can tell it is one.</summary>
	private readonly Mutex _instance;

	/// <summary>
	/// Asks GitHub, every few hours, whether a newer release exists. Null in a build that was not made by the
	/// release workflow, which is every build but a release: MinVer stamps a working tree with a version that
	/// reads as behind the latest release however far ahead of it the code is, so a developer would be told to
	/// upgrade to what they are sitting on top of, at every start.
	/// </summary>
	private readonly UpdateCheck? _updates;

	private readonly FailureNotices _failures = new();
	private readonly string _memoryPath;

	/// <summary>Whether each relay's process started before the broker did, by process id, for the restart report.</summary>
	private readonly Dictionary<int, bool> _outlived = [];

	/// <summary>The commit this install ran before this run, read before this run overwrites it.</summary>
	private readonly string? _previousCommit;

	/// <summary>
	/// The release this run announced, so a memory that could not be written costs a repeat at the next start
	/// and never within this one.
	/// </summary>
	private string? _announcedRelease;

	/// <summary>Why the icon could not be added yet, held until there is a logger to say it to.</summary>
	private string? _iconProblem;

	private ILogger _logger = NullLogger.Instance;
	private IServiceProvider? _services;
	private DateTimeOffset _servingSince;
	private RestartReport? _restart;
	private BrokerState _broker = BrokerState.Starting;
	private string? _failure;
	private int _notesAnswered;
	private int? _checkoutAhead;
	private bool _checkoutReading;
	private int _agents;
	private int _workspaces;
	private int _debuggedApps;
	private DateTimeOffset _lastOpened = DateTimeOffset.MinValue;
	private TrayAction? _balloonAction;
	private TrayIconState? _shownState;
	private string? _shownTooltip;
	private bool _stopped;
	private bool _disposed;

	/// <summary>
	/// Set when Windows refused a change to the startup registration, and cleared by the next menu, which is
	/// the one that says so.
	/// </summary>
	private bool _startupRefused;

	/// <summary>
	/// Creates the icon, in the starting state, and starts reading. An icon Windows will not add yet is tried
	/// again rather than thrown; what does throw is a failure that would not clear by waiting, which the caller
	/// treats as fatal.
	/// </summary>
	public TrayIconHost(App app)
	{
		_app = app;
		_queue = DispatcherQueue.GetForCurrentThread();

		_memoryPath = TrayMemoryFile.PathIn(Path.GetDirectoryName(RoseSettingsFile.PathFor()) ?? Path.GetTempPath());
		_previousCommit = TrayMemoryFile.Read(_memoryPath).LastCommitOf(Install);

		_art = new TrayIconArt(Path.Combine(AppContext.BaseDirectory, "Assets", "rose-mcp.ico"));

		_icon = new TaskbarIcon
		{
			ToolTipText = "RoseMCP",
			NoLeftClickDelay = true,
			ContextFlyout = _menu,
			LeftClickCommand = new MenuCommand(() => _queue.TryEnqueue(OnLeftClick)),
			RightClickCommand = new MenuCommand(PrepareMenu),
		};

		// Windows knows an icon by a GUID, which H.NotifyIcon derives from the executable's path, so a second
		// copy of the same exe -- started by hand while the one started at sign-in serves -- would take over the
		// first one's icon, and remove it on exit. The first copy keeps the derived GUID, which is what Windows
		// keys whether the icon is shown or hidden on; a second gets one of its own for as long as it runs.
		_instance = new Mutex(initiallyOwned: true, InstanceName(), out var firstCopy);
		if (!firstCopy) _icon.Id = TrayIcon.CreateUniqueGuidFromString($"{Environment.ProcessPath}|{Environment.ProcessId}");

		// Windows refuses to add an icon while there is no notification area to add it to -- at sign-in before
		// Explorer is up, or while it restarts -- and that clears by itself, so it is no reason for the broker
		// not to serve. The icon is tried again on a timer, and H.NotifyIcon adds it itself when the taskbar
		// announces it has started.
		_iconRetry = _queue.CreateTimer();
		_iconRetry.Interval = IconRetryInterval;
		_iconRetry.Tick += (_, _) => RetryIcon();
		if (!TryCreateIcon()) _iconRetry.Start();

		_clicks = _icon.TrayIcon.MessageWindow.SubscribeToMouseEventReceived(OnMouse);

		// A crash that was contained still happened, and the icon is the only place a person would hear of it.
		CrashHandler.Reported += OnReported;

		_poll = _queue.CreateTimer();
		_poll.Interval = PollInterval;
		_poll.Tick += (_, _) => Poll();
		_poll.Start();

		_checkoutTimer = _queue.CreateTimer();
		_checkoutTimer.Interval = CheckoutInterval;
		_checkoutTimer.Tick += (_, _) => ReadCheckout();
		_checkoutTimer.Start();

		var assembly = typeof(TrayIconHost).Assembly;
		if (UpdateCheckPolicy.EnabledFor(assembly, Environment.GetEnvironmentVariable(UpdateCheckPolicy.EnvironmentVariable)))
		{
			_updates = new UpdateCheck(HostVersion.Of(assembly));
			_updateTimer = _queue.CreateTimer();
			_updateTimer.Interval = UpdateCheck.Interval;
			_updateTimer.Tick += (_, _) => CheckForUpdate();
			_updateTimer.Start();

			CheckForUpdate();
		}

		Show();
	}

	/// <summary>
	/// The broker is taking calls. Starts the restart report, which says what the restart did to any agent that
	/// reconnects in the next half-minute, and remembers this build as the one that ran.
	/// </summary>
	public void Serving(IServiceProvider services)
	{
		if (_stopped) return;

		_services = services;
		_logger = services.GetRequiredService<ILogger<TrayIconHost>>();
		_broker = BrokerState.Serving;
		_servingSince = DateTimeOffset.UtcNow;
		_restart = new RestartReport();

		ReportIconProblem();

		// Off the UI thread, since the change can wait a moment on another tray's.
		var path = _memoryPath;
		var logger = _logger;
		var since = _servingSince;

		_ = Task.Run(() =>
		{
			var remembered = TrayMemoryFile.Remember(path, memory => memory.WithLastBuild(Install, Build.Commit, since));
			if (!remembered) logger.LogWarning("Could not write {Path}; the next restart cannot say which build it moved from.", path);
		});

		Poll();
		ReadCheckout();
	}

	/// <summary>
	/// The broker did not start. The icon stays, not serving, saying why, so the person can read the log and
	/// exit; one notification says so with the log.
	/// </summary>
	/// <param name="reason">Why, in a few words: "port 5077 is in use by another program".</param>
	/// <param name="logger">Where this tray's own lines go, the broker's logger being gone with it.</param>
	public void CouldNotStart(string reason, ILogger logger)
	{
		if (_stopped) return;

		_logger = logger;
		_services = null;
		_broker = BrokerState.Failed(reason);
		_failure = reason;

		ReportIconProblem();
		Notify(TrayNotification.CouldNotStart(reason));
		Show();
	}

	/// <summary>
	/// An agent answered a note the person sent it. Counted as waiting until the person opens RoseMCP, and
	/// said in a notification. The one way an answer to a note reaches the person while no window is open.
	/// </summary>
	/// <param name="agent">How the person knows the agent: its label, folder or client name.</param>
	/// <param name="answer">The start of what it said, or null to say only that it answered.</param>
	public void NoteAnswered(string agent, string? answer) => _queue.TryEnqueue(() =>
	{
		if (_stopped) return;

		_notesAnswered++;
		Notify(TrayNotification.NoteAnswered(agent, answer));
		Show();
	});

	/// <summary>
	/// The broker is about to stop. Every timer stops and the broker's services are let go before it does, and
	/// the icon says it is stopping until <see cref="Dispose"/> removes it.
	/// </summary>
	public void Stopping()
	{
		if (_stopped) return;

		_stopped = true;
		_poll.Stop();
		_checkoutTimer.Stop();
		_updateTimer?.Stop();
		_iconRetry.Stop();
		_services = null;
		_restart = null;
		_broker = BrokerState.Stopping;

		Show();
	}

	/// <summary>
	/// Removes the icon and unhooks everything that could call back into it. The crash handler's event is
	/// static and outlives this object, so it is unhooked first and whatever else fails.
	/// </summary>
	public void Dispose()
	{
		if (_disposed) return;

		CrashHandler.Reported -= OnReported;

		try
		{
			Stopping();
		}
		finally
		{
			_disposed = true;

			_clicks.Dispose();
			_updates?.Dispose();

			try
			{
				_icon.Dispose();
			}
			finally
			{
				_art.Dispose();

				// Closing the last handle to a mutex this thread owns destroys it, which is all releasing it would do.
				_instance.Dispose();
			}
		}
	}

	/// <summary>
	/// Adds the icon to the notification area, reporting whether it is there. A refusal is remembered to be said
	/// once there is a logger, and is not thrown: the shell not being ready is a state that passes.
	/// </summary>
	private bool TryCreateIcon()
	{
		try
		{
			// Efficiency mode off: it is meant for an app that hides in the tray doing nothing, and this process
			// is the broker every agent's calls go through. Throttled, every one of them would be slower.
			_icon.ForceCreate(enablesEfficiencyMode: false);

			if (_iconProblem is not null)
			{
				_logger.LogInformation("The tray icon has been added, after: {Problem}", _iconProblem);
				_iconProblem = null;
			}

			return true;
		}
		catch (InvalidOperationException exception)
		{
			_iconProblem ??= exception.Message;
			Debug.WriteLine($"Could not add the tray icon yet: {exception.Message}");
			return false;
		}
	}

	/// <summary>Tries the icon again until it is there, which H.NotifyIcon may also have done on the taskbar starting.</summary>
	private void RetryIcon()
	{
		var done = _disposed || _icon.IsCreated || TryCreateIcon();
		if (done) _iconRetry.Stop();
	}

	/// <summary>Says, once there is somewhere to say it, that the icon is not up yet.</summary>
	private void ReportIconProblem()
	{
		if (_iconProblem is not { } problem) return;

		_logger.LogWarning(
			"The tray icon could not be added yet ({Problem}); it is tried every {Interval} and again when the taskbar starts.",
			problem,
			IconRetryInterval);
	}

	/// <summary>
	/// The mutex a copy of this executable holds: per session, and per path, since a copy elsewhere is a
	/// different exe with a GUID of its own and no icon to take over.
	/// </summary>
	private static string InstanceName()
	{
		var path = (Environment.ProcessPath ?? "RoseMcp.Tray").ToUpperInvariant();
		var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)));

		return $@"Local\RoseMcp.Tray.{hash[..16]}";
	}

	/// <summary>Reads the broker's tables, gathers the restart report while it settles, and puts the result on the icon.</summary>
	private void Poll()
	{
		if (_stopped || _services is not { } services) return;

		var clients = services.GetRequiredService<ConnectedClients>().Describe();
		var workspaces = services.GetRequiredService<WorkspaceManager>().Describe();
		var sessions = services.GetRequiredService<LiveAppSessionManager>().Describe();

		var counted = new TrayFacts { Build = Build, Broker = _broker }.WithCounts(clients, workspaces, sessions);
		_agents = counted.Agents;
		_workspaces = counted.Workspaces;
		_debuggedApps = counted.DebuggedApps;

		Settle(clients);
		Show();
	}

	/// <summary>
	/// Feeds the restart report until its window has passed, then says what it found, once, and drops it.
	/// </summary>
	private void Settle(IReadOnlyList<ClientSummary> clients)
	{
		if (_restart is not { } report) return;

		report.Observe(clients.Where(OutlivedTheLastBroker));

		var settled = DateTimeOffset.UtcNow - _servingSince >= RestartReport.SettleWindow;
		if (!settled) return;

		_restart = null;
		_outlived.Clear();

		if (report.Notification(Build, _previousCommit) is { } notification) Notify(notification);
	}

	/// <summary>When this process started, in local time as <see cref="Process.StartTime"/> gives every process's.</summary>
	private static readonly DateTime Started = StartOfThisProcess();

	private static DateTime StartOfThisProcess()
	{
		using var self = Process.GetCurrentProcess();
		return self.StartTime;
	}

	/// <summary>
	/// Whether a client's relay was running before this process started, so it reconnected rather than
	/// connected. Measured against this process rather than the moment the broker began serving, so a session
	/// started while the broker was still starting is not taken for one that came back. A relay whose process
	/// cannot be read is taken as new, which errs towards saying nothing.
	/// </summary>
	private bool OutlivedTheLastBroker(ClientSummary client)
	{
		if (client.RelayProcessId is not { } pid) return false;
		if (_outlived.TryGetValue(pid, out var known)) return known;

		bool outlived;

		try
		{
			using var relay = Process.GetProcessById(pid);
			outlived = relay.StartTime < Started;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
		{
			outlived = false;
		}

		_outlived[pid] = outlived;
		return outlived;
	}

	/// <summary>
	/// Asks how far the checkout this build was made in has moved on, without waiting on the UI thread: the
	/// reader answers within its own budget and the answer lands whenever it does.
	/// <para>
	/// A count that comes back unknown keeps the last one known. The reader answers "still being counted"
	/// when git is slow, and dropping the item for that would have it blink in and out of the menu.
	/// </para>
	/// </summary>
	private async void ReadCheckout()
	{
		if (_stopped || _checkoutReading || _services is not { } services) return;

		_checkoutReading = true;

		try
		{
			var distance = await services.GetRequiredService<CheckoutDistanceReader>().ReadAsync(CancellationToken.None).ConfigureAwait(true);
			if (_stopped) return;

			if (distance is null) _checkoutAhead = null;
			else if (distance.Head.Count is { } ahead) _checkoutAhead = ahead;

			Show();
		}
		catch (ObjectDisposedException)
		{
			// The broker stopped while git was being asked, and the reader went with it.
		}
		catch (Exception exception)
		{
			_logger.LogInformation(exception, "Could not read how far the checkout has moved on.");
		}
		finally
		{
			_checkoutReading = false;
		}
	}

	/// <summary>
	/// Asks once, in the background, and announces a release the person has not been told about. Each release
	/// is announced once across runs and across every tray on the machine: the first to claim it in the shared
	/// memory announces it, and this run also keeps what it announced, so a memory that cannot be written costs
	/// a repeat on the next start and never within this one.
	/// <para>
	/// Nothing is downloaded and nothing is installed. The package is a hundred megabytes and installing it
	/// stops a tray that is holding somebody's loaded solution -- which is not a thing to do to them mid-task
	/// because a background poll noticed a tag.
	/// </para>
	/// </summary>
	private async void CheckForUpdate()
	{
		if (_updates is null || _stopped) return;

		var status = await _updates.CheckAsync().ConfigureAwait(true);
		if (_stopped) return;

		// Logged either way. A background poll that only writes something down when it fails is a feature
		// nobody can tell is working, and the question people arrive with is why they were never offered one.
		if (status.Problem is { Length: > 0 } problem)
		{
			_logger.LogInformation("Update check could not answer: {Problem}", problem);
		}
		else
		{
			_logger.LogInformation(
				"Update check: running {Running}, latest {Latest}, update available: {Available}",
				HostVersion.Of(typeof(TrayIconHost).Assembly),
				status.LatestVersion ?? "unknown",
				status.IsUpdateAvailable);
		}

		var newer = status.IsUpdateAvailable && status.LatestVersion is { Length: > 0 } && !string.Equals(status.LatestVersion, _announcedRelease, StringComparison.OrdinalIgnoreCase);

		if (!newer)
		{
			Show();
			return;
		}

		// Claimed in the file as it is now, so another tray on this machine that announced it first wins and this
		// one stays quiet. Off the UI thread, since the claim can wait a moment on another tray's change.
		var path = _memoryPath;
		var version = status.LatestVersion!;
		var (claimed, remembered) = await Task.Run(() => (TrayMemoryFile.ClaimRelease(path, version, out var written), written)).ConfigureAwait(true);
		if (_stopped) return;

		if (claimed)
		{
			_announcedRelease = status.LatestVersion;
			if (!remembered) _logger.LogWarning("Could not write {Path}; the release may be announced again at the next start.", _memoryPath);

			Notify(TrayNotification.Release(status.LatestVersion!));
		}

		Show();
	}

	/// <summary>A failure the crash handler contained, from whichever thread it happened on.</summary>
	private void OnReported(string message) => _queue.TryEnqueue(() => Failed(message));

	/// <summary>
	/// Keeps a failure waiting in the tooltip and menu until the person opens the log, and announces it unless
	/// it was announced already or another was a moment ago.
	/// </summary>
	private void Failed(string message)
	{
		if (_disposed) return;

		_failure = message;

		if (_failures.Admit(message, DateTimeOffset.UtcNow)) Notify(TrayNotification.Failure(message));

		Show();
	}

	/// <summary>
	/// Shows a notification, and remembers what clicking it does. One at a time: a later one replaces an
	/// earlier one on screen, and so does what a click does.
	/// </summary>
	private void Notify(TrayNotification notification)
	{
		if (_disposed) return;

		_logger.LogInformation("Notifying: {Title}. {Body}", notification.Title, notification.Body);
		_balloonAction = notification.Action;

		try
		{
			_icon.ShowNotification(
				notification.Title,
				notification.Body,
				notification.Kind == TrayNotificationKind.Failure ? NotificationIcon.Error : NotificationIcon.Info,
				customIconHandle: null,
				largeIcon: false,
				sound: true,
				respectQuietTime: true,
				realtime: false,
				timeout: null);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Could not show a notification: {Title}", notification.Title);
		}
	}

	/// <summary>A click on the notification, which arrives through the icon's own message window.</summary>
	private void OnMouse(object? sender, MessageWindow.MouseEventReceivedEventArgs args)
	{
		if (args.MouseEvent != MouseEvent.BalloonToolTipClicked) return;

		_queue.TryEnqueue(() =>
		{
			if (_disposed || _balloonAction is not { } action) return;

			_balloonAction = null;
			Do(action);
		});
	}

	/// <summary>Opens RoseMCP, or, where it cannot be opened, the menu, which says why.</summary>
	private void OnLeftClick()
	{
		if (_disposed) return;

		var canOpen = _broker.IsServing && InspectorLauncher.ResolvePath(_app.Options.InspectorPath) is not null;
		if (canOpen)
		{
			Do(TrayAction.OpenRose);
			return;
		}

		PrepareMenu();

		if (GetCursorPos(out var cursor)) _icon.ShowContextMenu(new System.Drawing.Point(cursor.X, cursor.Y));
	}

	/// <summary>Everything the icon shows, read now.</summary>
	private TrayFacts Facts() => new()
	{
		Build = Build,
		Broker = _broker,
		Agents = _agents,
		Workspaces = _workspaces,
		DebuggedApps = _debuggedApps,
		UpdateVersion = _updates?.Last is { IsUpdateAvailable: true, LatestVersion: { Length: > 0 } version } ? version : null,
		CheckoutAhead = _checkoutAhead,
		Failure = _failure,
		NotesAnswered = _notesAnswered,
	};

	/// <summary>Puts the state and the tooltip on the icon, touching it only for what changed.</summary>
	private void Show()
	{
		if (_disposed) return;

		var facts = Facts();
		var state = TrayStatus.StateOf(facts);
		var tooltip = TrayStatus.Tooltip(facts);

		// Never thrown out of: this is reached from Stopping on the way out, and an icon Windows has lost -- the
		// shell restarting, or another copy removing an icon it shared -- must not be why the broker is not stopped.
		try
		{
			if (state != _shownState)
			{
				// A copy, because the icon disposes the picture it held each time it is given another: handed the
				// art's own, the second change back to a state would hand it one it had already disposed.
				_icon.Icon = (System.Drawing.Icon)_art.For(state).Clone();
				_shownState = state;
			}

			if (!string.Equals(tooltip, _shownTooltip, StringComparison.Ordinal))
			{
				_icon.ToolTipText = tooltip;
				_shownTooltip = tooltip;
			}
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Could not update the tray icon to {State}.", state);
		}
	}

	/// <summary>
	/// Rebuilds the menu before the icon copies it.
	/// <para>
	/// The icon never shows this flyout. It builds a native popup menu from it on each right click, taking each
	/// item's text, <c>IsEnabled</c> and <c>IsChecked</c> and nothing else, and runs the chosen item's
	/// <c>Command</c> -- never a <c>Click</c>, never <c>Opening</c>. So each item gets a command, and the
	/// right-click command, which runs on the same message before the popup is built, is what stands in for
	/// <c>Opening</c>. Windows owns the startup answer and the RoseMCP app can be installed or not, so both are
	/// read here rather than cached.
	/// </para>
	/// <para>
	/// Each command is queued rather than run in place, so the icon has finished with its own menu before Exit
	/// disposes it.
	/// </para>
	/// </summary>
	private void PrepareMenu()
	{
		if (_disposed) return;

		var startup = new StartupState(StartupRegistration.IsEnabled, StartupRegistration.PointsElsewhere, _startupRefused);
		_startupRefused = false;

		var installed = InspectorLauncher.ResolvePath(_app.Options.InspectorPath) is not null;
		var entries = TrayStatus.Menu(Facts(), startup, installed);

		_menu.Items.Clear();

		foreach (var entry in entries)
		{
			_menu.Items.Add(Item(entry));
		}
	}

	private MenuFlyoutItemBase Item(TrayMenuEntry entry)
	{
		if (entry.Action == TrayAction.Separator) return new MenuFlyoutSeparator();

		var action = entry.Action;
		var command = new MenuCommand(() => _queue.TryEnqueue(() => Do(action)));

		if (entry.Checked is { } isChecked)
		{
			return new ToggleMenuFlyoutItem { Text = entry.Text, IsChecked = isChecked, IsEnabled = entry.Enabled, Command = command };
		}

		return new MenuFlyoutItem { Text = entry.Text, IsEnabled = entry.Enabled, Command = command };
	}

	/// <summary>Does what a menu item, a click on the icon or a click on a notification asked for.</summary>
	private void Do(TrayAction action)
	{
		if (_disposed) return;

		switch (action)
		{
			case TrayAction.OpenRose:
			case TrayAction.DebugAnApp:
				OpenRose();
				break;

			case TrayAction.OpenNoteAnswers:
				_notesAnswered = 0;
				OpenRose();
				break;

			case TrayAction.OpenRelease:
				OpenRelease();
				break;

			case TrayAction.OpenCheckout:
				if (Build.Checkout is { Length: > 0 } checkout) OpenInExplorer($"\"{checkout}\"");
				break;

			case TrayAction.OpenFailureLog:
				_failure = null;
				OpenFailureLog();
				break;

			case TrayAction.StartWithWindows:
				ToggleStartWithWindows();
				break;

			case TrayAction.OpenLogFolder:
				_failure = null;
				OpenLogFolder();
				break;

			case TrayAction.Exit:
				_ = _app.ExitAsync();
				return;
		}

		Show();
	}

	/// <summary>
	/// Starts the RoseMCP app against this broker, on whatever it finds. That app is the Inspector, which with
	/// no session named adopts the one debug session there is, or says there are none or several. "Debug an
	/// app" opens the same, because the app has no start page of its own to open on.
	/// <para>
	/// A launch that fails is a failure like any other, said with the log, rather than a tray that dies over an
	/// optional app and takes every warm worker with it.
	/// </para>
	/// </summary>
	private void OpenRose()
	{
		var now = DateTimeOffset.UtcNow;
		if (now - _lastOpened < ClickSettle) return;

		_lastOpened = now;

		try
		{
			using var started = InspectorLauncher.Launch(
				_app.Options.Host,
				_app.Options.Port,
				_app.OperatorToken,
				sessionId: null,
				_app.Options.InspectorPath);
		}
		catch (Exception exception) when (exception is FileNotFoundException or Win32Exception or InvalidOperationException)
		{
			_logger.LogError(exception, "Could not open RoseMCP.");
			Failed($"Could not open RoseMCP: {exception.Message}");
		}
	}

	/// <summary>
	/// Opens the release in a browser. Deliberately not a download: what to do about a new version is the
	/// person's call, and the install stops a broker that may be holding work of theirs.
	/// </summary>
	private void OpenRelease()
	{
		var url = _updates?.Last.ReleaseUrl ?? "https://github.com/AtomicBlom/RoseMCP/releases/latest";

		try
		{
			using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			// No default browser, or a shell association that refuses. Saying so beats a menu item that
			// appears to do nothing.
			_logger.LogWarning(exception, "Could not open {Url}.", url);
			Failed($"Could not open {url}: {exception.Message}");
		}
	}

	/// <summary>The log this process is writing, selected in Explorer, or the folder above it where there is none.</summary>
	private static void OpenFailureLog()
	{
		if (RoseFileLogging.Destination is { Length: > 0 } path && File.Exists(path))
		{
			OpenInExplorer($"/select,\"{path}\"");
			return;
		}

		OpenLogFolder();
	}

	/// <summary>
	/// The folder above this component's: Server, Worker, Tray and Inspector sit side by side under it, and
	/// the question that brings someone here is usually which of them has the answer.
	/// </summary>
	private static void OpenLogFolder()
	{
		var own = RoseLogFile.DirectoryFor("Tray", logRoot: Environment.GetEnvironmentVariable(RoseLogFile.RootVariable));
		var root = Path.GetDirectoryName(own) ?? own;

		Directory.CreateDirectory(root);
		OpenInExplorer($"\"{root}\"");
	}

	private void ToggleStartWithWindows()
	{
		// Off means off even when the registration belongs to another copy: the item was showing unticked,
		// so the click asks for on, and on means this executable.
		var wanted = !StartupRegistration.IsEnabled;

		if (StartupRegistration.Set(wanted)) return;

		// The menu is the only surface this has, so the next one to open carries the news, once.
		_startupRefused = true;
	}

	private static void OpenInExplorer(string arguments)
	{
		try
		{
			using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			Debug.WriteLine($"Could not open Explorer: {exception.Message}");
		}
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out CursorPoint point);

	[StructLayout(LayoutKind.Sequential)]
	private struct CursorPoint
	{
		public int X;
		public int Y;
	}

	/// <summary>
	/// An action as a command, for the tray icon, which invokes commands and never raises a click. Always
	/// executable: the native menu takes an item's enabled state from <c>IsEnabled</c>, not from here.
	/// </summary>
	private sealed class MenuCommand(Action execute) : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter) => execute();
	}
}
