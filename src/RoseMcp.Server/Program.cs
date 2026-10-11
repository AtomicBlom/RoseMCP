using System.Diagnostics;
using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;

namespace RoseMcp.Server;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		ServerOptions options;
		try
		{
			options = ServerOptions.Parse(args);
		}
		catch (ArgumentException exception)
		{
			await Console.Error.WriteLineAsync(exception.Message);
			await Console.Error.WriteLineAsync(
				"usage: RoseMcp.Server [--transport stdio|http] [--host <address>] [--port <n>] "
					+ "[--worker <path>] [--no-restore] [--no-relay]");

			return 2;
		}

		return options.UseHttp ? await RunHttpAsync(options) : await RunStdioAsync(options);
	}

	/// <summary>
	/// One client, one process, lifetime tied to it. This is how Claude Code launches an MCP server
	/// by default.
	/// <para>
	/// If a tray is already running, this process relays to it rather than starting workers of its
	/// own, so every session on the machine shares one warm worker per solution while each keeps the
	/// one thing only a stdio process has: the directory its client started it in.
	/// </para>
	/// <para>
	/// An installed relay never hosts the broker from its own older assemblies. With no tray, or a
	/// tray that answers and then refuses the session, it hands the session to the install's own
	/// broker, started as a child; where that will not start either, it ends the session and says why
	/// on stderr. Anything else hosts the broker here, as a Linux install or a build output does. See
	/// <see cref="RelayInstall"/>.
	/// </para>
	/// </summary>
	private static async Task<int> RunStdioAsync(ServerOptions options)
	{
		var installBroker = options.NoRelay ? null : RelayInstall.BrokerFor(AppContext.BaseDirectory);

		// Asked before anything is built, because the answer decides what to build.
		var trayListening = !options.NoRelay && await TrayRelay.IsListeningAsync(options, CancellationToken.None);

		foreach (var route in RelayInstall.RoutesFor(options.NoRelay, trayListening, installBroker is not null))
		{
			switch (route)
			{
				case StdioRoute.Tray:
					if (await RunRelayAsync(logging => TrayRelay.TryConnectAsync(options, logging, CancellationToken.None)) is { } relayed) return relayed;
					break;

				case StdioRoute.ChildBroker:
					if (await RunRelayAsync(logging => ConnectToChildAsync(options, installBroker!, logging)) is { } child) return child;
					break;

				case StdioRoute.Refuse:
					await Console.Error.WriteLineAsync(Refusal(options, installBroker!));
					return 1;

				default:
					return await HostBrokerAsync(options);
			}
		}

		throw new UnreachableException("Every list of routes ends in one that cannot fall through.");
	}

	/// <summary>Hosts the broker in this process, for a stdio client that launched it where it stands.</summary>
	private static async Task<int> HostBrokerAsync(ServerOptions options)
	{
		var builder = Host.CreateApplicationBuilder();
		ConfigureLogging(builder.Logging);

		builder.Services
			.AddRoseMcpBroker(broker =>
			{
				Apply(options, broker);

				// A stdio client launched this process where it stands, so the working directory is the caller's.
				broker.DefaultRootIsTheCaller = true;

				// And that client is the only one this broker will ever have, so a call without a session id is its.
				broker.ServesOneClient = true;
			})
			.WithStdioServerTransport();

		await builder.Build().RunAsync();
		return 0;
	}

	/// <summary>
	/// What an installed relay says on stderr when it can serve the session neither through the tray
	/// nor through the install's broker. Never stdout: that carries protocol frames and nothing else.
	/// </summary>
	private static string Refusal(ServerOptions options, string installBroker)
	{
		var brokerState = File.Exists(installBroker)
			? $"the install's broker at {installBroker} would not start (its reason is in this session's log)"
			: $"the install's broker is missing from {installBroker}, as it is while a deploy is copying it";

		return $"RoseMCP relay: no tray on port {options.Port} took this session, and {brokerState}. This relay runs from "
			+ "an install's relay folder and does not host a broker from its own assemblies, which are older than the "
			+ "install's and would hold its files through the next deploy. Start the tray, or let the deploy finish, and "
			+ "reconnect the MCP client (/mcp).";
	}

	/// <summary>
	/// Stdio in front, a broker behind. Nothing is declared here: both listing and calling are
	/// forwarded, so this cannot drift out of step with the tools the broker actually has.
	/// <para>
	/// It advertises <c>tools.listChanged</c> and hands the SDK the relay's signal as its tool
	/// collection, because the client talks only to this process: a broker advertising it is not
	/// enough, and the relay is what tells the client after a restart that the tools changed. It gives
	/// the broker's instructions as its own for the same reason.
	/// </para>
	/// </summary>
	/// <param name="connect">Opens the relay, or returns null so the caller can try its next route.</param>
	private static async Task<int?> RunRelayAsync(Func<ILoggerFactory, Task<TrayRelay?>> connect)
	{
		// One factory, shared with the host below, so a relayed session writes one log rather than
		// two. Registered as an instance, which the container does not dispose, so the using here
		// stays the only owner.
		using var logging = LoggerFactory.Create(ConfigureLogging);

		var relay = await connect(logging);

		// The tray answered the probe and then went away, or the child broker would not start: the
		// caller tries its next way of serving the session, which for an installed relay is never
		// hosting the broker here.
		if (relay is null) return null;

		await using (relay)
		{
			var builder = Host.CreateApplicationBuilder();
			builder.Logging.ClearProviders();
			builder.Services.AddSingleton<ILoggerFactory>(logging);

			builder.Services
				.AddMcpServer(server =>
				{
					server.ServerInfo = new()
					{
						Name = "rose-mcp",

						// This process's own, not the tray's. A relay declares none of the tools it forwards, so
						// the number worth telling a client is the one for the binary its client started.
						Version = BuildIdentity.Of(typeof(Program).Assembly).ToHandshake(),
					};

					server.ServerInstructions = relay.Instructions;
					server.Capabilities = new() { Tools = new() { ListChanged = true } };
					server.ToolCollection = relay.ToolsChanged;

					// A client whose roots change says so; the relay asks again and tells the broker.
					server.Handlers.NotificationHandlers =
					[
						new(ClientIntroduction.RootsChangedNotification, (_, _) =>
						{
							relay.RootsChanged();
							return ValueTask.CompletedTask;
						}),
					];
				})
				.WithStdioServerTransport()

				// First, and here rather than in the tray: this is the outermost Rose process a relayed
				// call reaches, so the id it mints is the one the tray and the worker repeat.
				.WithCallCorrelation()

				// Each request carries who the client is, which the relay tells the broker once there is
				// someone to name; the request itself does not wait on it.
				.WithListToolsHandler((context, token) =>
				{
					relay.Introduce(context.Server);
					return relay.ListToolsAsync(token);
				})
				.WithCallToolHandler((context, token) =>
				{
					relay.Introduce(context.Server);
					return relay.CallToolAsync(context.Params!, context.Server, token);
				})

				// This is an MCP boundary like any other, and declaring no tools of its own is exactly
				// why it was missed: a boundary is wherever an exception meets the SDK. Without it a
				// relay failure the SDK does not recognise came back as "An error occurred invoking
				// 'rose_x'." -- the shrug this filter exists to replace, on the one path where the
				// caller most needs to be told the tray is gone.
				.WithToolErrorMessages();

			// Not disposed here: RunAsync disposes the host when it finishes.
			var host = builder.Build();
			var running = host.RunAsync();

			// A broker this relay started has exited, which a deploy does to it: the session cannot
			// continue, so it ends rather than answering every call with the same failure.
			if (await Task.WhenAny(running, relay.Ended) != running)
			{
				logging.CreateLogger(typeof(Program)).LogWarning(
					"The broker this session relayed to has stopped; ending the session so its client can start another.");

				await host.StopAsync();
			}

			await running;
		}

		return 0;
	}

	/// <summary>
	/// Starts the install's broker as a child and relays to it, or returns null where it will not
	/// start, and the session ends saying so: hosting a broker from the relay's own assemblies would
	/// answer from an older build and hold the install's files through the next deploy.
	/// </summary>
	private static async Task<TrayRelay?> ConnectToChildAsync(ServerOptions options, string broker, ILoggerFactory logging)
	{
		var logger = logging.CreateLogger(typeof(Program));
		var upstream = new ChildBrokerUpstream(broker, options.ChildBrokerArguments(), Environment.CurrentDirectory, logging);

		try
		{
			var relay = await TrayRelay.ConnectAsync(
				upstream, Environment.CurrentDirectory, logging, TimeSpan.FromSeconds(60), CancellationToken.None);

			logger.LogInformation("No tray is running; relaying to a broker started from {Broker}.", broker);

			return relay;
		}
		catch (Exception exception)
		{
			await upstream.DisposeAsync();

			logger.LogWarning(
				exception,
				"Could not start the install's broker at {Broker}, so this session ends: the relay does not host one from its "
					+ "own assemblies, which are as old as the last relay protocol change.",
				broker);

			return null;
		}
	}

	/// <summary>
	/// Long-lived and shared. Because the broker outlives any single client session, a reconnecting
	/// client reattaches to solutions that are already loaded -- warm workers across restarts,
	/// without any worker outliving the broker that owns it.
	/// </summary>
	private static async Task<int> RunHttpAsync(ServerOptions options)
	{
		var builder = WebApplication.CreateBuilder();
		ConfigureLogging(builder.Logging);

		builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");

		// One token for both surfaces. ROSEMCP_TOKEN, when it is set, is what gates the whole server
		// -- including the MCP endpoint -- and the operator API is gated on the same value, because a
		// second secret for the same person on the same loopback port would be two things to get
		// wrong rather than one.
		//
		// Minted before the services rather than after building them, because the inspector has to be
		// handed it: a presenter registered without the token could start a window the server would
		// then refuse.
		var operatorToken = OperatorToken.FromEnvironmentOrMint(out var minted);

		// Before AddRoseMcpBroker, so its TryAdd of the do-nothing presenter stands down. This host
		// has an endpoint and a token, which is the whole precondition for opening an inspector.
		builder.Services.AddSingleton<IInspectorPresenter>(services => new OperatorInspector(
			options.Host,
			options.Port,
			operatorToken,
			services.GetRequiredService<ILogger<OperatorInspector>>()));

		// Idle workers are evicted over http and not over stdio. A stdio broker's workers end with
		// its one client; this one outlives every client, as the tray does, and would otherwise keep
		// each solution any of them opened loaded until it is stopped.
		builder.Services
			.AddRoseMcpBroker(broker =>
			{
				Apply(options, broker);
				broker.IdleEvictionAfter = BrokerOptions.LongLivedIdleEviction;
			})
			.WithHttpTransport();

		var application = builder.Build();

		// First, and before the token, because it costs a header read and refuses a whole class of caller
		// no later check would notice: a browser sends the token nowhere but sends Origin always.
		application.Use(RefuseForeignOrigin);

		if (!minted) application.Use(RequireToken(operatorToken));

		application.MapMcp();

		// What the broker has loaded, its debug sessions, and who it is to a relay -- the same routes
		// the tray maps, from one place.
		application.MapRoseAdminApi();

		application.MapRoseOperatorApi(operatorToken);

		// Said once, and only when this process chose it. A token nobody set is a token nobody can
		// use, and an operator surface that cannot be reached because its secret was never announced
		// would look like a bug in the surface. Where ROSEMCP_TOKEN set it, whoever set it knows.
		if (minted)
		{
			application.Logger.LogInformation(
				"The operator API at {Prefix} is open for this run with token {Token}. It changes every time this "
					+ "process starts.",
				OperatorApi.Prefix,
				operatorToken.Value);
		}

		await application.RunAsync();
		return 0;
	}

	/// <summary>
	/// Refuses a request whose <c>Origin</c> names anywhere but this machine, which the MCP
	/// specification asks of a local http server. See <see cref="LoopbackOrigin"/> for the attack and
	/// for why an absent header is allowed.
	/// </summary>
	private static async Task RefuseForeignOrigin(HttpContext context, RequestDelegate next)
	{
		if (!LoopbackOrigin.IsAllowed(context.Request.Headers.Origin.ToString()))
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		await next(context);
	}

	/// <summary>
	/// Refuses every request that does not carry the token. Gates the whole server, the MCP endpoint
	/// included, which is what makes a non-loopback bind allowable at all.
	/// <para>
	/// The comparison goes through <see cref="OperatorToken"/> rather than a string <c>!=</c>, so the
	/// same fixed-time check guards both surfaces. A secret compared with ordinary string equality
	/// leaks its prefix through timing, and having one of the two doors do it right was the wrong
	/// half to leave.
	/// </para>
	/// </summary>
	private static Func<HttpContext, RequestDelegate, Task> RequireToken(OperatorToken token) => async (context, next) =>
	{
		if (!token.Matches(context.Request.Headers.Authorization.ToString()))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

		await next(context);
	};

	/// <summary>
	/// Every log goes to stderr. In stdio mode stdout carries protocol frames and nothing else; a
	/// single stray write corrupts the stream and surfaces as an unintelligible protocol error.
	/// </summary>
	private static void ConfigureLogging(ILoggingBuilder logging)
	{
		logging.ClearProviders();
		logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

		// And to disk, because in stdio mode stderr belongs to whichever client launched us and is
		// not somewhere a person can go back and read.
		logging.AddRoseFileLogging("Server");
	}

	private static void Apply(ServerOptions options, BrokerOptions broker)
	{
		broker.WorkerPath = options.WorkerPath;
		broker.NoRestore = options.NoRestore;
	}
}
