using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Ui;

namespace RoseMcp.Tray;

/// <summary>
/// Hosts the broker in-process and shows the tray UI over it.
/// <para>
/// In-process, not talking to a separate server, and that is the point: the window reads the live
/// WorkspaceManager directly. There is no second copy of the state to drift, and no polling
/// protocol to write. Launching this app is what starting the server in http mode means.
/// </para>
/// </summary>
public partial class App : Application
{
	private WebApplication? _broker;
	private MainWindow? _window;

	public App() => InitializeComponent();

	/// <summary>The broker's services, which the window reads its rows from.</summary>
	public IServiceProvider Services => _broker?.Services
		?? throw new InvalidOperationException("The broker has not started yet.");

	public TrayOptions Options { get; private set; } = new();

	/// <summary>
	/// The secret this run's operator API is gated on, and what the inspector is launched with.
	/// <para>
	/// Minted here rather than read from the environment, because the tray is the thing that knows
	/// the person asking is at the keyboard: it hands the token only to a process it starts itself.
	/// A new one every run, so an inspector left open from yesterday is told to reopen from the tray
	/// rather than silently authorised.
	/// </para>
	/// </summary>
	public OperatorToken OperatorToken { get; } = OperatorToken.Mint();

	protected override async void OnLaunched(LaunchActivatedEventArgs args)
	{
		// Every line of this is inside the try. It is an async void override, so nothing awaits it and
		// nothing catches what it throws: a port already in use, or an unparsable --port, ends the
		// process with no window, no dialog and no log line. That is the worst way for this app to
		// fail, because what a user notices is every warm worker on the machine going with it.
		try
		{
			Options = TrayOptions.Parse(Environment.GetCommandLineArgs());

			var builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();
			builder.Logging.AddDebug();

			// AddDebug alone writes nowhere unless a debugger is attached, which is never true of the
			// tray a user actually runs, so this is what gives the process a record of itself at all.
			builder.Logging.AddRoseFileLogging("Tray");
			builder.WebHost.UseUrls($"http://{Options.Host}:{Options.Port}");

			builder.Services
				.AddRoseMcpBroker(broker => broker.WorkerPath = Options.WorkerPath)
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
			_broker.MapGet(
				"/admin/workspaces",
				(WorkspaceManager workspaces) => Results.Json(workspaces.Describe(), ContractJson.Options));

			// The debug sessions beside the workspaces, from the same shared manager, exactly as the http
			// server maps them. Two hosts serving the same broker should not answer different questions.
			_broker.MapGet(
				"/admin/sessions",
				(LiveAppSessionManager sessions) => Results.Json(sessions.Describe(), ContractJson.Options));

			// The operator surface every RoseMCP window reads a session through, gated on this run's
			// token. Mapped after the admin endpoints and before the broker starts, which is the only
			// ordering requirement: middleware and routes both have to be in place before a request
			// can arrive.
			_broker.MapRoseOperatorApi(OperatorToken);

			await _broker.StartAsync();

			// After the broker, because its services hold the logger, and before the window, so a
			// failure while the window is being built has somewhere to go.
			CrashHandler.Attach(this, Services.GetRequiredService<ILogger<App>>());

			_window = new MainWindow();
			_window.Activate();
		}
		catch (Exception exception)
		{
			Fail(exception);
		}
	}

	/// <summary>
	/// Reports a startup failure and stops, having no window to report it in.
	/// <para>
	/// The log is the only place this can go. It writes through a factory of its own rather than the
	/// broker's, because the failure worth catching here happens before the broker starts and a
	/// logger taken from the broker's services would be unavailable exactly then.
	/// </para>
	/// </summary>
	private void Fail(Exception exception)
	{
		try
		{
			using var logging = LoggerFactory.Create(builder => builder.AddRoseFileLogging("Tray"));

			logging.CreateLogger<App>().LogCritical(
				exception,
				"RoseMCP could not start on http://{Host}:{Port}. Its log is at {Log}.",
				Options.Host,
				Options.Port,
				RoseFileLogging.Destination ?? "(nowhere)");
		}
		catch (Exception)
		{
			// Nothing left to report with, which is the state this used to be in for every startup
			// failure. Exiting quietly is no worse than before, and the attempt costs nothing.
		}

		Exit();
	}

	/// <summary>
	/// Stops the broker, which closes every worker's stdin and so takes the workers with it. Leaving
	/// Roslyn hosts behind holding whole solutions in memory is invisible until a machine runs out.
	/// </summary>
	public async Task ShutdownAsync()
	{
		if (_broker is not null) await _broker.StopAsync();

		Exit();
	}
}
