using System.IO.Pipelines;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// An agent session in front of a relay: an MCP client talking over pipes to a server wired to the relay
/// the way the stdio host wires it, so what the relay learns of its client it learns from real requests.
/// </summary>
internal sealed class RelayFront : IAsyncDisposable
{
	private readonly McpServer _server;
	private readonly CancellationTokenSource _stop;
	private readonly Task _running;

	private RelayFront(McpClient agent, McpServer server, CancellationTokenSource stop, Task running)
	{
		Agent = agent;
		_server = server;
		_stop = stop;
		_running = running;
	}

	/// <summary>The agent's side: what a client such as Claude Code holds.</summary>
	public McpClient Agent { get; }

	/// <summary>A client calling itself <paramref name="name"/>, on the newest protocol revision unless <paramref name="protocolVersion"/> names another.</summary>
	public static McpClientOptions Options(string name, string version, string? protocolVersion = null, params string[] roots)
	{
		var options = new McpClientOptions
		{
			ClientInfo = new Implementation { Name = name, Version = version },
			ProtocolVersion = protocolVersion,
		};

#pragma warning disable MCP9005 // Roots are deprecated by the newest revision and still answered on the ones before it.
		if (roots.Length > 0)
		{
			options.Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } };
			options.Handlers = new McpClientHandlers
			{
				RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
				{
					Roots = [.. roots.Select(root => new Root { Uri = new Uri(root).AbsoluteUri })],
				}),
			};
		}
#pragma warning restore MCP9005

		return options;
	}

	/// <summary>Opens an agent session on <paramref name="relay"/>.</summary>
	public static async Task<RelayFront> OpenAsync(TrayRelay relay, McpClientOptions options, CancellationToken cancellationToken)
	{
		var toServer = new Pipe();
		var toClient = new Pipe();

		var serverOptions = new McpServerOptions
		{
			ServerInfo = new Implementation { Name = "relay-front", Version = "1.0.0" },
			Handlers = new McpServerHandlers
			{
				ListToolsHandler = (context, token) =>
				{
					relay.Introduce(context.Server);
					return relay.ListToolsAsync(token);
				},
				CallToolHandler = (context, token) =>
				{
					relay.Introduce(context.Server);
					return relay.CallToolAsync(context.Params!, context.Server, token);
				},
				NotificationHandlers =
				[
					new(ClientIntroduction.RootsChangedNotification, (_, _) =>
					{
						relay.RootsChanged();
						return ValueTask.CompletedTask;
					}),
				],
			},
		};

		var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), serverOptions);
		var stop = new CancellationTokenSource();
		var running = server.RunAsync(stop.Token);

		var agent = await McpClient.CreateAsync(
			new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
			options,
			cancellationToken: cancellationToken);

		return new RelayFront(agent, server, stop, running);
	}

	public async ValueTask DisposeAsync()
	{
		await Agent.DisposeAsync();
		await _stop.CancelAsync();

		try
		{
			await _running;
		}
		catch (OperationCanceledException)
		{
			// Stopped, as asked.
		}

		await _server.DisposeAsync();
		_stop.Dispose();
	}
}
