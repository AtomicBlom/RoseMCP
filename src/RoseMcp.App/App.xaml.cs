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

		CrashHandler.Attach(this, _logging.CreateLogger<App>());

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
	/// neither named -- "Debug an app" -- an Inspector window already open is brought forward rather than a second
	/// opened, since a window with no session settles on the one session there is, and that is the one it shows.
	/// </summary>
	public void ShowInspector(string? sessionId, int? targetProcessId)
	{
		var named = sessionId is { Length: > 0 } || targetProcessId is not null;
		var open = named
			? _inspectors.FirstOrDefault(window => window.IsAbout(sessionId, targetProcessId))
			: _inspectors.LastOrDefault();

		if (open is not null)
		{
			open.ShowSession(sessionId);
			open.BringToFront();
			return;
		}

		var window = new InspectorWindow(Client, sessionId, targetProcessId, OptionsProblem);
		window.Closed += (_, _) => _inspectors.Remove(window);

		_inspectors.Add(window);
		window.Activate();
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
			var options = Read(CommandLine.Split(raw), out _);

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
