using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RoslynMcp.Worker;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		WorkerOptions options;
		try
		{
			options = WorkerOptions.Parse(args);
		}
		catch (ArgumentException ex)
		{
			await Console.Error.WriteLineAsync($"{ex.Message}{Environment.NewLine}usage: RoslynMcp.Worker --solution <path> [--no-restore]");
			return 2;
		}

		var builder = Host.CreateApplicationBuilder(args);

		// Nothing may reach stdout but protocol frames. A stray write corrupts the stream and the
		// failure surfaces as an unintelligible protocol error, so route every log to stderr.
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton<RestoreRunner>();
		builder.Services.AddSingleton<SolutionLoader>();
		builder.Services.AddSingleton<DiagnosticsService>();
		builder.Services.AddSingleton<WorkspaceHost>();
		builder.Services.AddHostedService(services => services.GetRequiredService<WorkspaceHost>());
		builder.Services
			.AddMcpServer(server => server.ServerInfo = new() { Name = "roslyn-mcp-worker", Version = ThisAssembly.Version })
			.WithStdioServerTransport()
			.WithToolsFromAssembly();

		await builder.Build().RunAsync();
		return 0;
	}
}

internal static class ThisAssembly
{
	public const string Version = "0.1.0";
}
