namespace RoslynMcp.Server;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

		builder.Services.AddMcpServer().WithHttpTransport();

		var app = builder.Build();
		app.MapMcp();

		await app.RunAsync();
		return 0;
	}
}
