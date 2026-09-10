using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace RoseMcp.Ui;

/// <summary>
/// Catches what a WinUI app would otherwise die of silently, writes it to the log, and offers it to
/// whoever can put it on screen.
/// <para>
/// A desktop app with no handler here exits with no window, no dialog and no log line, which is the
/// worst way for one to fail. It matters more than usual for the tray, because that process owns
/// every warm worker on the machine: an unobserved exception in a click handler would take a
/// solution's whole compilation with it.
/// </para>
/// <para>
/// The XAML exception is marked handled, deliberately. Almost everything that reaches it here is an
/// <c>async void</c> event handler that failed at something the window can survive not doing --
/// a request that did not answer, a clipboard that refused. Letting the process go for that trades
/// a broken button for a broken app.
/// </para>
/// </summary>
public static class CrashHandler
{
	/// <summary>
	/// Raised with a sentence fit to show a person, for each failure caught. Subscribe from a window
	/// to put it in an information bar; the log has it either way, so a window that is not up yet
	/// costs nothing.
	/// </summary>
	public static event Action<string>? Reported;

	/// <summary>
	/// Hooks the three places an unobserved exception surfaces in a WinUI app. Call once, after the
	/// logger exists.
	/// </summary>
	public static void Attach(Application application, ILogger logger)
	{
		application.UnhandledException += (_, args) =>
		{
			logger.LogCritical(args.Exception, "Unhandled exception on the UI thread: {Message}", args.Message);

			// Handled, so the window survives a failed handler. See the note on the type.
			args.Handled = true;

			Report($"Something failed and was contained: {args.Exception.Message}");
		};

		// Not handleable -- the process is going either way -- so the only job here is to leave a
		// line behind saying why, which is otherwise the one thing a crash like this does not do.
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			if (args.ExceptionObject is Exception exception)
			{
				logger.LogCritical(exception, "Unhandled exception; the process is terminating.");
			}
		};

		// A Task nobody awaited. Marked observed for the same reason the XAML one is handled: the
		// default is to tear the process down at a garbage collection, long after the code that
		// caused it, which is close to undiagnosable.
		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			logger.LogError(args.Exception, "A task failed and nothing awaited it.");
			args.SetObserved();
		};
	}

	private static void Report(string message) => Reported?.Invoke(message);
}
