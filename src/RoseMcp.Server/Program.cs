using RoseMcp.Broker;
using RoseMcp.Contracts;

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
	/// </summary>
	private static async Task<int> RunStdioAsync(ServerOptions options)
	{
		var builder = Host.CreateApplicationBuilder();
		ConfigureLogging(builder.Logging);

		builder.Services
			.AddRoseMcpBroker(broker => Apply(options, broker))
			.WithStdioServerTransport();

		await builder.Build().RunAsync();
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
	}

	private static void Apply(ServerOptions options, BrokerOptions broker)
	{
		broker.WorkerPath = options.WorkerPath;
		broker.NoRestore = options.NoRestore;
	}
}
