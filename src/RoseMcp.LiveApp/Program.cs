using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;
using RoseMcp.Logging;

namespace RoseMcp.LiveApp;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		// The from-birth UWP path relaunches this same executable as the app's registered debugger, in
		// stub mode. That run has no MCP server and no target of its own; it only relays the app's ids
		// to the waiting host and resumes it, so it short-circuits everything below.
		if (Array.IndexOf(args, UwpResumeStub.ModeFlag) >= 0)
		{
			return UwpResumeStub.Run(args);
		}

		LiveAppOptions options;
		try
		{
			options = LiveAppOptions.Parse(args);
		}
		catch (ArgumentException ex)
		{
			await Console.Error.WriteLineAsync(
				$"{ex.Message}{Environment.NewLine}usage: RoseMcp.LiveApp (--attach <pid> | --launch <path> | --launch-uwp <aumid>) [--arguments <args>] [--description <text>]");
			return 2;
		}

		var builder = Host.CreateApplicationBuilder(args);

		// Nothing may reach stdout but protocol frames; route every log to stderr and a file.
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
		builder.Logging.AddRoseFileLogging("LiveApp", options.LogDiscriminator);

		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton<LiveAppSessionHost>();
		builder.Services.AddHostedService(services => services.GetRequiredService<LiveAppSessionHost>());
		builder.Services
			.AddMcpServer(server => server.ServerInfo = new()
			{
				Name = "rose-mcp-live-app",
				Version = BuildIdentity.Of(typeof(Program).Assembly).ToHandshake(),
			})
			.WithStdioServerTransport()
			.WithToolsFromAssembly(typeof(Program).Assembly, ToolJson.Readable(McpJsonUtilities.DefaultOptions))

			// First, so every line written for a call carries the id the broker sent with it -- the
			// one search that finds the same call in the broker's file and this one.
			.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
			{
				using var correlation = CallCorrelation.Begin(context.Params?.Meta);

				return await next(context, cancellationToken);
			}))
			.WithRequestFilters(filters => filters.AddCallToolFilter(CursorStamp.Filter))
			.WithAbsolutePathArguments()
			.WithToolErrorMessages();

		await builder.Build().RunAsync();
		return 0;
	}
}
