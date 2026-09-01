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
				.AddMcpServer(server => server.ServerInfo = new() { Name = "rose-mcp", Version = "0.1.0" })
				.WithStdioServerTransport()
				.WithListToolsHandler((_, token) => relay.ListToolsAsync(token))
				.WithCallToolHandler((context, token) => relay.CallToolAsync(context.Params!, context.Server, token));

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

		var token = Environment.GetEnvironmentVariable("ROSEMCP_TOKEN");
		if (!string.IsNullOrWhiteSpace(token)) application.Use(RequireToken(token));

		application.MapMcp();

		// Exactly what the tray window renders, so the two cannot disagree.
		application.MapGet(
			"/admin/workspaces",
			(WorkspaceManager workspaces) => Results.Json(workspaces.Describe(), ContractJson.Options));

		await application.RunAsync();
		return 0;
	}

	private static Func<HttpContext, RequestDelegate, Task> RequireToken(string token) => async (context, next) =>
	{
		var supplied = context.Request.Headers.Authorization.ToString();
		if (supplied != $"Bearer {token}")
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
