using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using RoseMcp.App.Inspector;
using RoseMcp.Logging;
using RoseMcp.Ui;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.App;

/// <summary>
/// The RoseMCP app: one per broker, holding the RoseMCP window and an Inspector window for each debugged process
/// somebody opened, all reading the broker through one <see cref="OperatorClient"/>.
/// <para>
/// A launch names what it wants: nothing, for the RoseMCP window; a session, a process or <c>--inspect</c>, for an
/// Inspector window. A second launch against the same broker is redirected here rather than starting another app,
/// and is handled the same way -- and its token replaces the one this app holds, because a broker that restarted
/// minted a new one, and the tray's launch is the only way a new token reaches a window that outlived the old run.
/// The process ends when its last window closes.
/// </para>
/// </summary>
public partial class App : Application
{
	private readonly ILoggerFactory _logging = LoggerFactory.Create(builder => builder.AddRoseFileLogging("App"));
	private readonly List<InspectorWindow> _inspectors = [];
	private RoseWindow? _rose;
	private OperatorClient? _client;

	public App() => InitializeComponent();

	/// <summary>
	/// Where to connect and what this app was first opened on. Never null: a command line that cannot be read leaves
	/// the defaults in place and puts the reason on screen, because a window that explains itself is worth more than
	/// a process that exits before anyone sees why.
	/// </summary>
	public InspectorOptions Options { get; private set; } = new();

	/// <summary>Why the command line could not be read, when it could not.</summary>
	public string? OptionsProblem { get; private set; }

	public ILoggerFactory Logging => _logging;

	/// <summary>The one client every window reads the broker through. Disposed with nothing: it lives as long as the process.</summary>
	public OperatorClient Client => _client ?? throw new InvalidOperationException("The app has not launched yet.");

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		Options = Read(Environment.GetCommandLineArgs().Skip(1).ToArray(), out var problem);
		OptionsProblem = problem;

		var logger = _logging.CreateLogger<App>();
		CrashHandler.Attach(this, logger);
		logger.LogInformation(
			"The RoseMCP app started against {Broker}, {Token}, for {Window}.",
			Options.BaseAddress,
			Options.Token is null ? "with no token" : "with a token",
			Options.OpensInspector ? "an Inspector window" : "the RoseMCP window");

		_client = new OperatorClient(Options.BaseAddress, Options.Token);

		// A second launch redirects its activation here rather than opening an app of its own.
		AppInstance.GetCurrent().Activated += OnRedirected;

		Open(Options);
	}

	/// <summary>Opens the RoseMCP window, or brings it forward where it is open.</summary>
	public void ShowRose()
	{
		if (_rose is { } open)
		{
			open.BringToFront();
			return;
		}

		var window = new RoseWindow();
		window.Closed += (_, _) =>
		{
			if (ReferenceEquals(_rose, window)) _rose = null;
		};

		_rose = window;
		window.Activate();
	}

	/// <summary>
	/// Opens an Inspector window on a session, or brings forward the one already about that process or session. With
	/// neither named -- "Debug an app" -- a window already on its start page is brought forward rather than a second
	/// opened, and otherwise a new window opens on its start page, since every other window is about a process of
	/// its own.
	/// </summary>
	public void ShowInspector(string? sessionId, int? targetProcessId)
	{
		var named = sessionId is { Length: > 0 } || targetProcessId is not null;
		var open = named
			? _inspectors.FirstOrDefault(window => window.IsAbout(sessionId, targetProcessId))
			: _inspectors.LastOrDefault(window => window.IsOnStartPage);

		if (open is not null)
		{
			open.ShowSession(sessionId, targetProcessId);
			open.BringToFront();
			return;
		}

		InspectorWindow? created = null;
		var window = new InspectorWindow(
			Client,
			sessionId,
			targetProcessId,
			OptionsProblem,
			(shownSession, shownProcess) => ShownElsewhere(created, shownSession, shownProcess));
		created = window;
		window.Closed += (_, _) => _inspectors.Remove(window);

		_inspectors.Add(window);
		window.Activate();
	}

	/// <summary>
	/// Brings forward a window other than <paramref name="asking"/> that is already about the session, and says whether
	/// one was: a session opened from one window's start page that another window is showing is that window's.
	/// </summary>
	private bool ShownElsewhere(InspectorWindow? asking, string sessionId, int? targetProcessId)
	{
		var other = _inspectors.FirstOrDefault(candidate => !ReferenceEquals(candidate, asking) && candidate.IsAbout(sessionId, targetProcessId));
		if (other is null) return false;

		other.ShowSession(sessionId, targetProcessId);
		other.BringToFront();
		return true;
	}

	private void Open(InspectorOptions options)
	{
		if (options.OpensInspector) ShowInspector(options.SessionId, options.TargetProcessId);
		else ShowRose();
	}

	/// <summary>
	/// Takes a redirected launch: its token, where it carries a new one, and the window it asks for.
	/// <para>
	/// The event arrives on a background thread, so everything it touches is marshalled onto the UI thread first.
	/// A failure here is logged rather than thrown: a click on the tray icon that does nothing is a disappointment,
	/// and one that takes every window down is a bug report.
	/// </para>
	/// </summary>
	private void OnRedirected(object? sender, AppActivationArguments args)
	{
		try
		{
			// The redirecting process's own command line, not this one's: what is wanted is what the second launch
			// named, and this process was started without it.
			var raw = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
				? launch.Arguments
				: null;
			var logger = _logging.CreateLogger<App>();
			logger.LogInformation("A second launch was handed to this app, as {Kind}.", args.Kind);

			var options = Read(CommandLine.Split(raw), out var problem);

			// What was asked is logged and the token is not: the log is read by people the token is not for.
			if (problem is not null) logger.LogWarning("A second launch's command line could not be read: {Problem}", problem);
			else logger.LogInformation("A second launch asked for {Window}.", options.OpensInspector ? "an Inspector window" : "the RoseMCP window");

			Queue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
			{
				try
				{
					if (options.Token is { Length: > 0 } token && Client.Reauthorise(token))
					{
						Options = Options with { Token = token };
						_logging.CreateLogger<App>().LogInformation("Took a new operator token from a redirected launch.");
						_rose?.Reconnect();
					}

					Open(options);
				}
				catch (Exception exception)
				{
					_logging.CreateLogger<App>().LogWarning(exception, "A redirected activation could not be applied.");
				}
			});
		}
		catch (Exception exception)
		{
			_logging.CreateLogger<App>().LogWarning(exception, "A redirected activation could not be read.");
		}
	}

	/// <summary>The UI thread's queue, captured when the app launched.</summary>
	private DispatcherQueue Queue { get; } = DispatcherQueue.GetForCurrentThread();

	/// <summary>
	/// Reads a command line, reporting what went wrong rather than throwing. The window shows the
	/// problem; there is nowhere else for it to go before one exists.
	/// </summary>
	private static InspectorOptions Read(string[] arguments, out string? problem)
	{
		try
		{
			problem = null;
			return InspectorOptions.Parse(arguments);
		}
		catch (ArgumentException exception)
		{
			problem = exception.Message;
			return new InspectorOptions();
		}
	}
}
