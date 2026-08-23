using Microsoft.Extensions.DependencyInjection;

using RoslynMcp.Broker.Tools;

namespace RoslynMcp.Broker;

/// <summary>
/// The single registration path, shared by the console host and the tray app.
/// <para>
/// Having one of these is the reason the broker is a library. Two hosts each wiring up their own
/// services is two chances for them to disagree about what is loaded.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
	public static IMcpServerBuilder AddRoslynMcpBroker(
		this IServiceCollection services,
		Action<BrokerOptions>? configure = null)
	{
		if (configure is not null) services.Configure(configure);

		// Singleton, so every session shares one set of workers. In http mode that is what lets a
		// reconnecting client reattach to an already-loaded solution rather than reload it.
		services.AddSingleton<WorkspaceManager>();

		return services
			.AddMcpServer(server => server.ServerInfo = new() { Name = "roslyn-mcp", Version = "0.1.0" })
			.WithTools<BrokerTools>()
			.WithTools<BrokerAnalysisTools>();
	}
}
