using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Logging;

namespace RoseMcp.Worker;

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
			await Console.Error.WriteLineAsync($"{ex.Message}{Environment.NewLine}usage: RoseMcp.Worker --solution <path> [--no-restore]");
			return 2;
		}

		var builder = Host.CreateApplicationBuilder(args);

		// Nothing may reach stdout but protocol frames. A stray write corrupts the stream and the
		// failure surfaces as an unintelligible protocol error, so route every log to stderr.
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

		// The same stream, kept on disk. A worker outlives the call that started it and dies with
		// its broker, so stderr is gone by the time anyone asks what it did.
		builder.Logging.AddRoseFileLogging("Worker", options.SolutionPath);

		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton<ShadowCopyAnalyzerAssemblyLoader>();
		builder.Services.AddSingleton<RestoreRunner>();
		builder.Services.AddSingleton<SharedWorkProgress>();
		builder.Services.AddSingleton<Xaml.XamlStubReports>();
		builder.Services.AddSingleton<SolutionLoader>();
		builder.Services.AddSingleton<DiagnosticsService>();
		builder.Services.AddSingleton<CodeFixCatalog>();
		builder.Services.AddSingleton<WorkspaceHost>();
		builder.Services.AddHostedService(services => services.GetRequiredService<WorkspaceHost>());
		builder.Services
			.AddMcpServer(server => server.ServerInfo = new() { Name = "rose-mcp-worker", Version = ThisAssembly.Version })
			.WithStdioServerTransport()
			.WithToolsFromAssembly();

		await builder.Build().RunAsync();
		return 0;
	}
}
