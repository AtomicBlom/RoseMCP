using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Tray;

/// <summary>
/// Hosts the broker in-process and shows the tray UI over it.
/// <para>
/// In-process, not talking to a separate server, and that is the point: the window reads the live
/// WorkspaceManager directly. There is no second copy of the state to drift, and no polling
/// protocol to write. Launching this app is what starting the server in http mode means.
/// </para>
/// </summary>
public partial class App : Application
{
	private WebApplication? _broker;
	private MainWindow? _window;

	public App() => InitializeComponent();

	/// <summary>The broker's services, which the window reads its rows from.</summary>
	public IServiceProvider Services => _broker?.Services
		?? throw new InvalidOperationException("The broker has not started yet.");

	public TrayOptions Options { get; private set; } = new();

	protected override async void OnLaunched(LaunchActivatedEventArgs args)
	{
		Options = TrayOptions.Parse(Environment.GetCommandLineArgs());

		var builder = WebApplication.CreateBuilder();
		builder.Logging.ClearProviders();
		builder.Logging.AddDebug();
		builder.WebHost.UseUrls($"http://{Options.Host}:{Options.Port}");

		builder.Services
			.AddRoseMcpBroker(broker => broker.WorkerPath = Options.WorkerPath)
			.WithHttpTransport();

		_broker = builder.Build();
		_broker.MapMcp();
		_broker.MapGet(
			"/admin/workspaces",
			(WorkspaceManager workspaces) => Results.Json(workspaces.Describe(), ContractJson.Options));

		await _broker.StartAsync();

		_window = new MainWindow();
		_window.Activate();
	}

	/// <summary>
	/// Stops the broker, which closes every worker's stdin and so takes the workers with it. Leaving
	/// Roslyn hosts behind holding whole solutions in memory is invisible until a machine runs out.
	/// </summary>
	public async Task ShutdownAsync()
	{
		if (_broker is not null) await _broker.StopAsync();

		Exit();
	}
}
