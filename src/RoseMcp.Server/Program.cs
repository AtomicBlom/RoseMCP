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
					+ "[--worker <path>] [--no-restore]");

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
	/// </summary>
	private static async Task<int> RunStdioAsync(ServerOptions options)
	{
		// Asked before anything is built, because the answer decides what to build.
		if (await TrayRelay.IsListeningAsync(options, CancellationToken.None))
		{
			var relayed = await RunRelayAsync(options);
			if (relayed is { } code) return code;
		}

		var builder = Host.CreateApplicationBuilder();
		ConfigureLogging(builder.Logging);

		builder.Services
			.AddRoseMcpBroker(broker => Apply(options, broker))
			.WithStdioServerTransport();

		await builder.Build().RunAsync();
		return 0;
	}

	/// <summary>
	/// Stdio in front, the tray's broker behind. Nothing is declared here: both listing and calling
	/// are forwarded, so this cannot drift out of step with the tools the tray actually has.
	/// </summary>
	private static async Task<int?> RunRelayAsync(ServerOptions options)
	{
		// One factory, shared with the host below, so a relayed session writes one log rather than
		// two. Registered as an instance, which the container does not dispose, so the using here
		// stays the only owner.
		using var logging = LoggerFactory.Create(ConfigureLogging);

		var relay = await TrayRelay.TryConnectAsync(options, logging, CancellationToken.None);

		// The tray answered the probe and then went away. Rare, and the honest response is to fall
		// back to owning workers rather than to fail the session.
		if (relay is null) return null;

		await using (relay)
		{
			var builder = Host.CreateApplicationBuilder();
			builder.Logging.ClearProviders();
			builder.Services.AddSingleton<ILoggerFactory>(logging);

			builder.Services
				.AddMcpServer(server => server.ServerInfo = new()
				{
					Name = "rose-mcp",

					// This process's own, not the tray's. A relay declares none of the tools it forwards, so
					// the number worth telling a client is the one for the binary its client started.
					Version = HostVersion.Of(typeof(Program).Assembly),
				})
				.WithStdioServerTransport()
				.WithListToolsHandler((_, token) => relay.ListToolsAsync(token))
				.WithCallToolHandler((context, token) => relay.CallToolAsync(context.Params!, context.Server, token))

				// This is an MCP boundary like any other, and declaring no tools of its own is exactly
				// why it was missed: a boundary is wherever an exception meets the SDK. Without it a
				// relay failure the SDK does not recognise came back as "An error occurred invoking
				// 'rose_x'." -- the shrug this filter exists to replace, on the one path where the
				// caller most needs to be told the tray is gone.
				.WithToolErrorMessages();

			await builder.Build().RunAsync();
		}

		return 0;
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
		builder.Services.AddRoseMcpBroker(broker => Apply(options, broker)).WithHttpTransport();

		var application = builder.Build();

		// First, and before the token, because it costs a header read and refuses a whole class of caller
		// no later check would notice: a browser sends the token nowhere but sends Origin always.
		application.Use(RefuseForeignOrigin);

		// One token for both surfaces. ROSEMCP_TOKEN, when it is set, is what gates the whole server
		// -- including the MCP endpoint -- and the operator API is gated on the same value, because a
		// second secret for the same person on the same loopback port would be two things to get
		// wrong rather than one.
		var operatorToken = OperatorToken.FromEnvironmentOrMint(out var minted);
		if (!minted) application.Use(RequireToken(operatorToken));

		application.MapMcp();

		// Exactly what the tray window renders, so the two cannot disagree.
		application.MapGet(
			"/admin/workspaces",
			(WorkspaceManager workspaces) => Results.Json(workspaces.Describe(), ContractJson.Options));

		// The live-app sessions alongside the workspaces, from the same shared manager.
		application.MapGet(
			"/admin/sessions",
			(LiveAppSessionManager sessions) => Results.Json(sessions.Describe(), ContractJson.Options));

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
