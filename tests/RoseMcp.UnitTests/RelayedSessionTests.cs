using System.ComponentModel;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// The tray's http transport serves each request without a session, so every client relayed to it was the
/// same caller: an acknowledgement taken from one would have quieted a reason for every agent, and so was
/// refused for all of them. The relay names the session it serves, and the broker's call filter takes that
/// name for the call's session.
/// <para>
/// Through a real relay and a real broker on a loopback port, so the name is minted, sent in <c>_meta</c> and
/// read by the same filter a tool call meets in the tray. A probe tool stands in for the workspace tools,
/// since a workspace needs a worker process this suite does not start; what it does with the session is what
/// a read's notice is computed from.
/// </para>
/// </summary>
public sealed class RelayedSessionTests
{
	private static readonly string Shop = Path.Combine(Path.GetTempPath(), "rose-relayed", "Shop.slnx");

	private static readonly WorkspaceReason Legacy = WorkspaceReason.Of(
		ReasonKinds.ProjectNotLoaded,
		HealthEffect.MayAffectSomeProjects,
		"Project Legacy.Tests did not load successfully; its semantic results are unreliable.",
		["Legacy.Tests"]);

	[Test]
	public async Task A_relayed_acknowledgement_quiets_that_sessions_reads_and_no_other(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);
		await using var mine = await tray.RelayAsync(cancellationToken);
		await using var theirs = await tray.RelayAsync(cancellationToken);

		(await Text(mine, Probe.Acknowledge, cancellationToken)).ShouldBe("acknowledged");

		(await Text(mine, Probe.Read, cancellationToken)).ShouldBe("quiet", "the session that acknowledged it hears nothing");
		(await Text(theirs, Probe.Read, cancellationToken)).ShouldStartWith("This workspace is degraded", Case.Sensitive);
	}

	/// <summary>A client of the stateless transport that names no session is any client, so acknowledging from it is still refused.</summary>
	[Test]
	public async Task A_call_naming_no_session_over_http_is_still_refused(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);
		await using var direct = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions { Endpoint = tray.Endpoint }),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		var result = await direct.CallToolAsync(Probe.Acknowledge, cancellationToken: cancellationToken);

		result.IsError.ShouldBe(true);
		Content(result).ShouldContain("serves several clients", Case.Sensitive);
	}

	/// <summary>The broker takes a relay's name only in the shape a relay mints, and the transport's own session over it.</summary>
	[Test]
	public void A_session_is_the_transports_else_the_relays_else_none()
	{
		var minted = CallSession.Mint();

		CallSession.Of(null, Marked(minted)).ShouldBe($"relayed-{minted}");
		CallSession.Of("transport-session", Marked(minted)).ShouldBe("transport-session");
		CallSession.Of(null, null).ShouldBeNull();
		CallSession.Of(null, Marked("short")).ShouldBeNull();
		CallSession.Of(null, Marked("NOT-HEX-AT-ALL-0123456789ABCDEF")).ShouldBeNull();
		CallSession.Mint().ShouldNotBe(minted);

		static JsonObject Marked(string id) => new() { [CallSession.MetaKey] = id };
	}

	private static async Task<string> Text(TrayRelay relay, string tool, CancellationToken cancellationToken) =>
		Content(await relay.CallToolAsync(new CallToolRequestParams { Name = tool }, client: null!, cancellationToken));

	private static string Content(CallToolResult result) =>
		string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

	/// <summary>What a workspace tool does with the session, without a workspace: acknowledge, then compose a read's notice.</summary>
	[McpServerToolType]
	public sealed class Probe(WorkspaceHealth health)
	{
		public const string Acknowledge = "probe_acknowledge";

		public const string Read = "probe_read";

		[McpServerTool(Name = Acknowledge), Description("Acknowledges the probe reason for the calling session.")]
		public string AcknowledgeLegacy()
		{
			health.Acknowledge(Shop, WorkspaceStatusReporter.HealthOf([Legacy])[0]);
			return "acknowledged";
		}

		[McpServerTool(Name = Read), Description("The degraded notice a read would carry for the calling session.")]
		public string ReadNotice() => WorkspaceStatusReporter.DegradedNotice([Legacy], health.HeardFor(Shop)) ?? "quiet";
	}

	/// <summary>A broker serving MCP over http on a loopback port, as the tray does, with the probe beside its tools.</summary>
	private sealed class Tray : IAsyncDisposable
	{
		private readonly WebApplication _app;

		private Tray(WebApplication app, Uri endpoint)
		{
			_app = app;
			Endpoint = endpoint;
		}

		public Uri Endpoint { get; }

		public static async Task<Tray> StartAsync(TemporaryLocalAppData home, CancellationToken cancellationToken)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Services
				.AddRoseMcpBroker(broker => broker.LocalAppData = home.Path)
				.WithTools<Probe>()
				.WithHttpTransport();

			var app = builder.Build();
			app.MapMcp();

			// What a relay probes for before it connects, as the tray answers it.
			app.MapGet("/admin/workspaces", () => Results.Json(Array.Empty<WorkspaceSummary>(), ContractJson.Options));

			await app.StartAsync(cancellationToken);

			return new Tray(app, new Uri(app.Urls.First() + "/"));
		}

		/// <summary>A relay in front of this broker, as a stdio session started while the tray runs is.</summary>
		public async Task<TrayRelay> RelayAsync(CancellationToken cancellationToken)
		{
			var options = new ServerOptions { Host = Endpoint.Host, Port = Endpoint.Port };

			return await TrayRelay.TryConnectAsync(options, NullLoggerFactory.Instance, cancellationToken)
				?? throw new InvalidOperationException($"No relay could reach the broker at {Endpoint}.");
		}

		public async ValueTask DisposeAsync()
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
