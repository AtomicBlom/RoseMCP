using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
		using var startup = LoggerFactory.Create(ConfigureLogging);

		var relay = await TrayRelay.TryConnectAsync(options, startup, CancellationToken.None);
		if (relay is not null) return await RunRelayAsync(options, relay);

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
	private static async Task<int> RunRelayAsync(ServerOptions options, TrayRelay relay)
	{
		await using (relay)
		{
			var builder = Host.CreateApplicationBuilder();
			ConfigureLogging(builder.Logging);

			builder.Services
				.AddMcpServer(server => server.ServerInfo = new() { Name = "rose-mcp", Version = "0.1.0" })
				.WithStdioServerTransport()
				.WithListToolsHandler((_, token) => relay.ListToolsAsync(token))
				.WithCallToolHandler((context, token) =>
					relay.CallToolAsync(context.Params!, ProgressFor(context), token));

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
	/// Bridges progress across the relay: notifications the tray sends us are re-sent to our own
	/// client under the token it asked for. Without this a load looks like a hang, because the calls
	/// that take real time are exactly the ones whose progress would be dropped in the middle.
	/// </summary>
	private static IProgress<ProgressNotificationValue>? ProgressFor(RequestContext<CallToolRequestParams> context) =>
		context.Params?.ProgressToken is { } token ? new RelayProgress(context.Server, token) : null;

	private sealed class RelayProgress(McpServer endpoint, ProgressToken token)
		: IProgress<ProgressNotificationValue>
	{
		public void Report(ProgressNotificationValue value) =>
			_ = endpoint.NotifyProgressAsync(token, value);
	}

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
