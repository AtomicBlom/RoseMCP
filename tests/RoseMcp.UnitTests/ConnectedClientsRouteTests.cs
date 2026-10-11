using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// Who is connected, end to end: real relays in front of a real broker on a loopback port, each with an agent
/// session in front of it, and the table read back from <c>GET /admin/clients</c> as a window reads it.
/// </summary>
public sealed class ConnectedClientsRouteTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Two sessions started in one folder are two relays and two clients; the many calls one session's subagents
	/// make through its one relay are one client's; and a client that came without a relay is listed unnamed.
	/// </summary>
	[Test]
	public async Task Relayed_sessions_are_named_and_a_client_without_a_relay_is_not(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(cancellationToken);
		await using var mine = await tray.RelayAsync(cancellationToken);
		await using var theirs = await tray.RelayAsync(cancellationToken);

		await using var me = await RelayFront.OpenAsync(mine, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);
		await using var them = await RelayFront.OpenAsync(theirs, RelayFront.Options("claude-code", "2.1.301"), cancellationToken);

		await me.Agent.ListToolsAsync(cancellationToken: cancellationToken);
		await Task.WhenAll(
			me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken).AsTask(),
			me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken).AsTask(),
			me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken).AsTask());

		await them.Agent.ListToolsAsync(cancellationToken: cancellationToken);
		await them.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);

		await using var direct = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions { Endpoint = tray.Endpoint }),
			new McpClientOptions
			{
				DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
				ClientInfo = new Implementation { Name = "direct-client", Version = "0.1" },
			},
			cancellationToken: cancellationToken);

		await direct.ListToolsAsync(cancellationToken: cancellationToken);
		await direct.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);

		var rows = await tray.ClientsAsync(
			clients => clients.Count(client => client is { Named: true, Watching: true, ClientName: not null }) == 2,
			cancellationToken);

		rows.Count.ShouldBe(3);

		var named = rows.Where(row => row.Named).ToList();
		named.Select(row => row.WorkingDirectory).ShouldAllBe(directory => directory == Directory.GetCurrentDirectory());
		named.ShouldAllBe(row => row.RelayProcessId == Environment.ProcessId && row.ToolsCurrent == true);

		var myRow = named.Single(row => row.ClientVersion == "2.1.300");
		myRow.Calls.ShouldBe(3, "three calls from one session are one client's");
		myRow.LastCallUtc.ShouldNotBeNull();
		named.Single(row => row.ClientVersion == "2.1.301").Calls.ShouldBe(1);

		var unnamed = rows.Single(row => !row.Named);
		unnamed.Calls.ShouldBe(1);
		unnamed.ClientName.ShouldBe("direct-client", "an unnamed client's clientInfo is what its transport carried");
		unnamed.Watching.ShouldBeNull();
		unnamed.ToolsCurrent.ShouldBe(true, "it listed the tools from this broker");
	}

	/// <summary>The broker refuses an introduction it cannot trust, saying why, and keeps nothing of it.</summary>
	[Test]
	public async Task A_malformed_introduction_is_refused_and_kept_nowhere(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(cancellationToken);
		using var http = new HttpClient { BaseAddress = tray.Endpoint };

		using var noRelay = await http.PostAsJsonAsync(
			RelayProtocol.ClientRoute, new RelayClient { Session = CallSession.Mint() }, ContractJson.Options, cancellationToken);
		noRelay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await noRelay.Content.ReadAsStringAsync(cancellationToken)).ShouldContain(RelayProtocol.RelayHeader, Case.Sensitive);

		using var badSession = new HttpRequestMessage(HttpMethod.Post, RelayProtocol.ClientRoute)
		{
			Content = JsonContent.Create(new RelayClient { Session = "not hex" }, options: ContractJson.Options),
		};
		badSession.Headers.Add(RelayProtocol.RelayHeader, RelayProtocol.NewRelayId());
		(await http.SendAsync(badSession, cancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		using var huge = new HttpRequestMessage(HttpMethod.Post, RelayProtocol.ClientRoute)
		{
			Content = new StringContent(new string(' ', 128 * 1024), System.Text.Encoding.UTF8, "application/json"),
		};
		huge.Headers.Add(RelayProtocol.RelayHeader, RelayProtocol.NewRelayId());
		(await http.SendAsync(huge, cancellationToken)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

		(await http.GetFromJsonAsync<List<ClientSummary>>("/admin/clients", ContractJson.Options, cancellationToken)).ShouldBeEmpty();
	}

	/// <summary>A tool to call, standing in for the broker's own, which need a worker this suite does not start.</summary>
	[McpServerToolType]
	public sealed class Echo
	{
		public const string Name = "probe_echo";

		[McpServerTool(Name = Name), Description("Answers at once.")]
		public static string Answer() => "echo";
	}

	/// <summary>A broker serving MCP and the admin routes over http on a loopback port, as the tray does.</summary>
	private sealed class Tray : IAsyncDisposable
	{
		private readonly WebApplication _app;

		private Tray(WebApplication app, Uri endpoint)
		{
			_app = app;
			Endpoint = endpoint;
		}

		public Uri Endpoint { get; }

		public static async Task<Tray> StartAsync(CancellationToken cancellationToken)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Services
				.AddRoseMcpBroker()
				.WithTools<Echo>()
				.WithHttpTransport();

			var app = builder.Build();
			app.MapMcp();
			app.MapRoseAdminApi();

			await app.StartAsync(cancellationToken);

			return new Tray(app, new Uri(app.Urls.First() + "/"));
		}

		/// <summary>A relay in front of this broker, given a few attempts, since a loaded runner can overrun its two-second handshake.</summary>
		public async Task<TrayRelay> RelayAsync(CancellationToken cancellationToken)
		{
			var options = new ServerOptions { Host = Endpoint.Host, Port = Endpoint.Port };

			for (var attempt = 0; attempt < 3; attempt++)
			{
				if (await TrayRelay.TryConnectAsync(options, NullLoggerFactory.Instance, cancellationToken) is { } relay) return relay;
			}

			throw new InvalidOperationException($"No relay could reach the broker at {Endpoint} in three attempts.");
		}

		/// <summary>The table, read until <paramref name="settled"/> holds of it: introductions and watches arrive on their own time.</summary>
		public async Task<IReadOnlyList<ClientSummary>> ClientsAsync(
			Func<IReadOnlyList<ClientSummary>, bool> settled,
			CancellationToken cancellationToken)
		{
			using var http = new HttpClient { BaseAddress = Endpoint };
			var deadline = DateTime.UtcNow + Patience;

			while (true)
			{
				var clients = await http.GetFromJsonAsync<List<ClientSummary>>("/admin/clients", ContractJson.Options, cancellationToken) ?? [];
				if (settled(clients)) return clients;

				if (DateTime.UtcNow > deadline)
				{
					throw new ShouldAssertException(
						$"The clients never settled; the last read was {System.Text.Json.JsonSerializer.Serialize(clients, ContractJson.Options)}");
				}

				await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			}
		}

		public async ValueTask DisposeAsync()
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
