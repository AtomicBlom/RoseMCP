using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using RoseMcp.Logging;
using RoseMcp.Ui;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector;

/// <summary>
/// Starts the one window, holds the options it was launched with, and routes a second launch's
/// arguments into the window already open.
/// </summary>
public partial class App : Application
{
	private readonly ILoggerFactory _logging = LoggerFactory.Create(builder => builder.AddRoseFileLogging("Inspector"));
	private MainWindow? _window;

	public App() => InitializeComponent();

	/// <summary>
	/// Where to connect and what to open on. Never null: a command line that cannot be read leaves
	/// the defaults in place and puts the reason on screen, because a window that explains itself is
	/// worth more than a process that exits before anyone sees why.
	/// </summary>
	public InspectorOptions Options { get; private set; } = new();

	/// <summary>Why the command line could not be read, when it could not.</summary>
	public string? OptionsProblem { get; private set; }

	public ILoggerFactory Logging => _logging;

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		Options = Read(Environment.GetCommandLineArgs().Skip(1).ToArray(), out var problem);
		OptionsProblem = problem;

		CrashHandler.Attach(this, _logging.CreateLogger<App>());

		// A second launch redirects its activation here rather than opening a window of its own,
		// which is what makes Inspect in the tray focus this window on the session it names.
		AppInstance.GetCurrent().Activated += OnRedirected;

		_window = new MainWindow();
		_window.Activate();
	}

	/// <summary>
	/// Takes a redirected launch's session id and shows it here.
	/// <para>
	/// The event arrives on a background thread, so everything it touches is marshalled onto the
	/// window's own queue first. It is also the one place the window can be asked to do something
	/// while it is mid-poll, so a failure here is logged rather than thrown: a second Inspect click
	/// that does nothing is a disappointment, and one that takes the window down is a bug report.
	/// </para>
	/// </summary>
	private void OnRedirected(object? sender, AppActivationArguments args)
	{
		try
		{
			var window = _window;
			if (window is null) return;

			// The redirecting process's own command line, not this one's: what is wanted is the
			// session the second Inspect click named, and this process was started without it.
			var raw = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
				? launch.Arguments
				: null;
			var session = Read(CommandLine.Split(raw), out _).SessionId;

			window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
			{
				if (session is not null) window.ShowSession(session);

				window.BringToFront();
			});
		}
		catch (Exception exception)
		{
			_logging.CreateLogger<App>().LogWarning(exception, "A redirected activation could not be applied.");
		}
	}

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
