using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The activity record end to end: an agent session behind a real relay calling a real broker on a loopback port,
/// and the record read back from <c>GET /operator/activity</c> as a window reads it -- attributed to the client the
/// table lists, with the failures in the caller's words, and without the reads that are not the agent's.
/// </summary>
public sealed class ActivityRouteTests : IDisposable
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "rose-activity-routes", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
			// The broker's file is closed as its host is disposed; a moment's lag leaves the folder to the temp sweep.
		}
	}

	[Test]
	public async Task A_relayed_sessions_calls_are_recorded_against_its_client_and_the_reads_that_are_not_its_are_not(
		CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);
		await using var relay = await tray.RelayAsync(cancellationToken);
		await using var session = await RelayFront.OpenAsync(relay, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);

		await session.Agent.ListToolsAsync(cancellationToken: cancellationToken);
		var client = await tray.SettledClientAsync(cancellationToken);

		await session.Agent.CallToolAsync(Echo.Name, new Dictionary<string, object?> { ["text"] = "hello" }, cancellationToken: cancellationToken);
		var failed = await session.Agent.CallToolAsync(Failing.Name, cancellationToken: cancellationToken);
		failed.IsError.ShouldBe(true);

		// A relay's own read, made under the session's name, is not one of the session's calls.
		await using var direct = await tray.DirectAsync(cancellationToken);
		await CancellableToolCall.InvokeAsync(
			direct, Echo.Name, new Dictionary<string, object?>(), progress: null, cancellationToken, session: CallSession.Mint(), relayRead: true);

		// A window's own reads are not MCP calls, and leave nothing behind.
		(await tray.Operator.GetAsync("/operator/clients", cancellationToken)).EnsureSuccessStatusCode();
		await tray.ActivityAsync(cancellationToken: cancellationToken);

		await tray.Record.FlushAsync(cancellationToken);
		var page = await tray.ActivityAsync(cancellationToken: cancellationToken);
		var calls = page.Entries.Where(entry => entry.Kind == ActivityEntryKind.Call).ToList();

		calls.Count.ShouldBe(2, "the echo and the failure, and neither the relay's read nor the window's");
		page.Entries.ShouldContain(entry => entry.Kind == ActivityEntryKind.BrokerStarted && entry.Broker == tray.Record.Broker);
		page.Directory.ShouldBe(_directory);

		calls.ShouldAllBe(call => call.ClientId == client.Id && call.ClientName == "claude-code" && call.ClientKey != null);
		calls.ShouldAllBe(call => call.Origin == Directory.GetCurrentDirectory() && call.CorrelationId != null);

		var echo = calls.Single(call => call.Tool == Echo.Name);
		echo.Outcome.ShouldBe(ActivityOutcome.Succeeded);
		echo.Response.ShouldBeNull("a listing leaves the bodies out");

		var failure = calls.Single(call => call.Tool == Failing.Name);
		failure.Outcome.ShouldBe(ActivityOutcome.Failed);
		failure.Error.ShouldNotBeNull().ShouldContain("The cart is empty.", Case.Sensitive);

		var full = await tray.Operator.GetFromJsonAsync<ActivityEntry>($"/operator/activity/{echo.Id}", ContractJson.Options, cancellationToken);
		full.ShouldNotBeNull().Response.ShouldBe("echo: hello");
		full.Arguments.ShouldNotBeNull().ShouldContain("\"text\":\"hello\"", Case.Sensitive);

		var mine = await tray.ActivityAsync($"client={client.Id}&tool={Echo.Name}&bodies=true", cancellationToken);
		mine.Bodies.ShouldBeTrue();
		mine.Entries.ShouldHaveSingleItem().Response.ShouldBe("echo: hello");

		(await tray.Operator.GetAsync("/operator/activity/0000000000000000", cancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
		(await tray.Anonymous.GetAsync("/operator/activity", cancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>Tools to call, standing in for the broker's own, which need a worker this suite does not start.</summary>
	[McpServerToolType]
	public sealed class Echo
	{
		public const string Name = "probe_echo";

		[McpServerTool(Name = Name), Description("Answers at once.")]
		public static string Answer(string? text = null) => $"echo: {text}";
	}

	[McpServerToolType]
	public sealed class Failing
	{
		public const string Name = "probe_fail";

		[McpServerTool(Name = Name), Description("Always fails.")]
		public static string Answer() => throw new InvalidOperationException("The cart is empty.");
	}

	/// <summary>A broker serving MCP, the admin routes and the operator routes over http on a loopback port, as the tray does.</summary>
	private sealed class Tray : IAsyncDisposable
	{
		private readonly WebApplication _app;

		private Tray(WebApplication app, Uri endpoint, OperatorToken token)
		{
			_app = app;
			Endpoint = endpoint;
			Anonymous = new HttpClient { BaseAddress = endpoint };
			Operator = new HttpClient { BaseAddress = endpoint };
			Operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
		}

		public Uri Endpoint { get; }

		public HttpClient Anonymous { get; }

		public HttpClient Operator { get; }

		public ActivityRecord Record => _app.Services.GetRequiredService<ActivityRecord>();

		public static async Task<Tray> StartAsync(string directory, CancellationToken cancellationToken)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Services
				.AddRoseMcpBroker(broker => broker.ActivityDirectory = directory)
				.WithTools<Echo>()
				.WithTools<Failing>()
				.WithHttpTransport();

			var app = builder.Build();
			var token = OperatorToken.Mint();
			app.MapMcp();
			app.MapRoseAdminApi();
			app.MapRoseOperatorApi(token);

			await app.StartAsync(cancellationToken);

			return new Tray(app, new Uri(app.Urls.First() + "/"), token);
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

		/// <summary>A client of this broker with no relay in front of it.</summary>
		public async Task<McpClient> DirectAsync(CancellationToken cancellationToken) =>
			await McpClient.CreateAsync(
				new HttpClientTransport(new HttpClientTransportOptions { Endpoint = Endpoint }),
				new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
				cancellationToken: cancellationToken);

		/// <summary>The relayed client's row, once its relay has said whom it serves, which arrives on its own time.</summary>
		public async Task<ClientSummary> SettledClientAsync(CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + Patience;

			while (true)
			{
				var clients = (await Operator.GetFromJsonAsync<ClientList>("/operator/clients", ContractJson.Options, cancellationToken))?.Clients ?? [];
				if (clients.SingleOrDefault(client => client is { Named: true, ClientName: not null }) is { } settled) return settled;

				if (DateTime.UtcNow > deadline) throw new ShouldAssertException("The relay never said whom it serves.");

				await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			}
		}

		public async Task<ActivityPage> ActivityAsync(string? query = null, CancellationToken cancellationToken = default) =>
			await Operator.GetFromJsonAsync<ActivityPage>(
				query is null ? "/operator/activity" : $"/operator/activity?{query}",
				ContractJson.Options,
				cancellationToken)
				?? throw new ShouldAssertException("The activity route answered nothing.");

		public async ValueTask DisposeAsync()
		{
			Anonymous.Dispose();
			Operator.Dispose();

			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
