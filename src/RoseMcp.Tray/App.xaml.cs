using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RoseMcp.Broker;
using RoseMcp.Logging;
using RoseMcp.Ui;

namespace RoseMcp.Tray;

/// <summary>
/// Hosts the broker in-process, with a notification icon and no window.
/// <para>
/// Launching this app is what starting the server in http mode means. It owns every warm worker on the
/// machine, so nothing that can crash on a person's click lives here: the windows are the RoseMCP app, a
/// process of its own that reads this broker over the operator API, started from the icon. See
/// <see href="../../docs/decisions/the-tray-process-owns-only-its-icon.md">the decision</see>.
/// </para>
/// </summary>
public partial class App : Application
{
	private WebApplication? _broker;
	private TrayIconHost? _tray;

	/// <summary>
	/// Where this tray's own lines go when the broker, whose logger they otherwise use, did not start. Kept
	/// for the life of the process, because the icon stays up after such a failure and can still fail again.
	/// </summary>
	private ILoggerFactory? _fallbackLogging;

	private bool _exiting;

	/// <summary>
	/// Whether the crash handler is hooked. It adds a handler each time it is attached, so a start that failed
	/// after attaching it is not allowed to attach it again and log every later failure twice.
	/// </summary>
	private bool _crashHandlerAttached;

	private void AttachCrashHandler(ILogger logger)
	{
		if (_crashHandlerAttached) return;

		_crashHandlerAttached = true;
		CrashHandler.Attach(this, logger);
	}

	public App()
	{
		InitializeComponent();

		// Not on the last window closing, which is the default: this process has no window, and is ended only
		// by Exit on its menu, or by whatever stops it from outside.
		DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
	}

	public TrayOptions Options { get; private set; } = new();

	/// <summary>
	/// The secret this run's operator API is gated on, and what the RoseMCP app is launched with.
	/// <para>
	/// Minted here rather than read from the environment, because the tray is the thing that knows
	/// the person asking is at the keyboard: it hands the token only to a process it starts itself.
	/// A new one every run, so a window left open from yesterday is told to reopen from the tray
	/// rather than silently authorised.
	/// </para>
	/// </summary>
	public OperatorToken OperatorToken { get; } = OperatorToken.Mint();

	protected override async void OnLaunched(LaunchActivatedEventArgs args)
	{
		// The icon first, before anything that can fail or take time, so that starting is a state a person
		// can see. Windows refusing to add it -- no notification area yet at sign-in -- is retried by the
		// icon itself while the broker serves; only a failure that waiting cannot clear ends the process,
		// and it does so before the broker holds anything.
		try
		{
			Options = TrayOptions.Parse(Environment.GetCommandLineArgs());
			_tray = new TrayIconHost(this);
		}
		catch (Exception exception)
		{
			Fail(exception);
			return;
		}

		// Every line of this is inside the try. It is an async void override, so nothing awaits it and
		// nothing catches what it throws: a port already in use would otherwise end the process with no
		// word anywhere, which is the worst way for this app to fail.
		try
		{
			var builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();
			builder.Logging.AddDebug();

			// AddDebug alone writes nowhere unless a debugger is attached, which is never true of the
			// tray a user actually runs, so this is what gives the process a record of itself at all.
			builder.Logging.AddRoseFileLogging("Tray");
			builder.WebHost.UseUrls($"http://{Options.Host}:{Options.Port}");

			// Before AddRoseMcpBroker, so its TryAdd of the do-nothing presenter stands down. This
			// host has an operator endpoint and a token, which is the whole precondition for being
			// able to open an inspector at all.
			builder.Services.AddSingleton<IInspectorPresenter>(services => new OperatorInspector(
				Options.Host,
				Options.Port,
				OperatorToken,
				services.GetRequiredService<ILogger<OperatorInspector>>(),
				Options.InspectorPath));

			// Idle workers are evicted here because the tray outlives every session it serves: without
			// it, each solution any session ever opened stays loaded until the tray is closed.
			builder.Services
				.AddRoseMcpBroker(broker =>
				{
					broker.WorkerPath = Options.WorkerPath;
					broker.IdleEvictionAfter = BrokerOptions.LongLivedIdleEviction;
				})
				.WithHttpTransport();

			_broker = builder.Build();

			// The same endpoint as the http server's, so the same refusal: a page cannot reach a broker
			// through a name it rebinds to loopback. See LoopbackOrigin for why an absent header is allowed.
			_broker.Use(async (context, next) =>
			{
				if (!LoopbackOrigin.IsAllowed(context.Request.Headers.Origin.ToString()))
				{
					context.Response.StatusCode = StatusCodes.Status403Forbidden;
					return;
				}

				await next(context);
			});

			_broker.MapMcp();

			// The workspaces, the debug sessions and the relay routes, mapped by the broker library exactly
			// as the http server maps them. Two hosts serving the same broker should not answer different
			// questions, and the relays a deploy counts reconnect here.
			_broker.MapRoseAdminApi();

			// The operator surface every RoseMCP window reads a session through, gated on this run's
			// token. Mapped after the admin endpoints and before the broker starts, which is the only
			// ordering requirement: middleware and routes both have to be in place before a request
			// can arrive.
			_broker.MapRoseOperatorApi(OperatorToken);

			await _broker.StartAsync();

			// After the broker, because its services hold the logger.
			AttachCrashHandler(_broker.Services.GetRequiredService<ILogger<App>>());

			// An Exit chosen while the broker was starting has already let the icon go.
			if (!_exiting) _tray.Serving(_broker.Services);
		}
		catch (Exception exception)
		{
			await NotServingAsync(exception);
		}
	}

	/// <summary>
	/// Records a broker that did not start, lets go of whatever of it did, and leaves the icon up saying
	/// so, rather than exiting: the person can read why, open the log and exit, and a tray that vanished
	/// at sign-in would say nothing at all.
	/// <para>
	/// The log is written through a factory of its own rather than the broker's, because the failure
	/// worth catching here happens before the broker starts and a logger taken from the broker's
	/// services would be unavailable exactly then.
	/// </para>
	/// </summary>
	private async Task NotServingAsync(Exception exception)
	{
		var portTaken = false;
		for (var cause = exception; cause is not null; cause = cause.InnerException)
		{
			portTaken |= cause is AddressInUseException;
		}

		var reason = portTaken
			? $"port {Options.Port} on {Options.Host} is in use by another program"
			: exception.Message.TrimEnd('.');

		_fallbackLogging ??= LoggerFactory.Create(builder => builder.AddRoseFileLogging("Tray"));
		var logger = _fallbackLogging.CreateLogger<App>();

		logger.LogCritical(
			exception,
			"RoseMCP could not start on http://{Host}:{Port}: {Reason}. Its log is at {Log}.",
			Options.Host,
			Options.Port,
			reason,
			RoseFileLogging.Destination ?? "(nowhere)");

		if (_broker is { } broker)
		{
			_broker = null;

			try
			{
				// A start that failed part of the way may have started some of the broker's services; they
				// are stopped here rather than left holding anything for the rest of the process's life.
				await broker.StopAsync();
				await broker.DisposeAsync();
			}
			catch (Exception stopping)
			{
				logger.LogWarning(stopping, "Stopping the broker that did not start failed as well.");
			}
		}

		AttachCrashHandler(logger);

		if (!_exiting) _tray?.CouldNotStart(reason, logger);
	}

	/// <summary>
	/// Reports a failure setting up the tray's icon that waiting would not clear, and stops, having nowhere to
	/// report it but the log. Only reached before the broker starts, so nothing is lost by exiting.
	/// </summary>
	private void Fail(Exception exception)
	{
		try
		{
			using var logging = LoggerFactory.Create(builder => builder.AddRoseFileLogging("Tray"));

			logging.CreateLogger<App>().LogCritical(
				exception,
				"RoseMCP could not set up its tray icon, so it is not starting. Its log is at {Log}.",
				RoseFileLogging.Destination ?? "(nowhere)");
		}
		catch (Exception)
		{
			// Nothing left to report with. Exiting quietly is all there is, and the attempt costs nothing.
		}

		Exit();
	}

	/// <summary>
	/// Stops the broker, which closes every worker's stdin and so takes the workers with it, then removes
	/// the icon and ends the process. Leaving Roslyn hosts behind holding whole solutions in memory is
	/// invisible until a machine runs out.
	/// <para>
	/// The icon is told first, so it stops reading the broker before the broker stops, and shows that it
	/// is stopping for as long as that takes. Everything after the first line is inside the try, so a
	/// failure anywhere -- the icon included -- still stops the broker, removes the icon and exits: an
	/// Exit that did nothing would leave a process nobody can end from its menu again.
	/// </para>
	/// </summary>
	public async Task ExitAsync()
	{
		if (_exiting) return;

		_exiting = true;

		try
		{
			_tray?.Stopping();

			if (_broker is not null) await _broker.StopAsync();
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"The tray did not stop cleanly: {exception.Message}");
		}
		finally
		{
			try
			{
				_tray?.Dispose();
				_fallbackLogging?.Dispose();
			}
			finally
			{
				Exit();
			}
		}
	}
}
