using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Settings;

namespace RoseMcp.UnitTests;

/// <summary>
/// A person starting a debug session without an agent, at <c>/operator/debug</c> and <c>/operator/sessions</c>, over a
/// real broker on a loopback port: every route behind the token, the running and installed apps a host reads, and every
/// refusal said before a host is started. The running and installed apps are a test's own, standing in for the
/// desktop, which is the host's to read and not this suite's.
/// </summary>
public sealed class DebugStartRouteTests
{
	[Test]
	[Arguments("GET", "/operator/debug/apps")]
	[Arguments("GET", "/operator/debug/packages")]
	[Arguments("GET", "/operator/debug/recent")]
	[Arguments("POST", "/operator/debug/attach")]
	[Arguments("POST", "/operator/debug/launch")]
	[Arguments("POST", "/operator/sessions/session-none/share")]
	[Arguments("DELETE", "/operator/sessions/session-none/share")]
	public async Task Every_debug_route_needs_the_operator_token(string method, string route, CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);

		using var request = new HttpRequestMessage(new HttpMethod(method), route) { Content = JsonContent.Create(new { }) };
		using var refused = await tray.Anonymous.SendAsync(request, cancellationToken);

		refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// The running apps are what the host reads, with a window unless every .NET process is asked for, none naming a
	/// session while nothing is being debugged; the packages likewise; and nothing debugged yet is nothing recent.
	/// </summary>
	[Test]
	public async Task The_start_page_reads_running_and_installed_apps_and_what_was_debugged_lately(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);

		var windowed = await tray.GetAsync<RunningAppList>("/operator/debug/apps", cancellationToken);
		windowed.Every.ShouldBeFalse();
		windowed.Apps.Select(RunningApps.DisplayName).ShouldBe(["Rose Probe"]);
		windowed.Apps[0].XamlStack.ShouldBe(XamlStack.WinUi);
		windowed.Apps[0].SessionId.ShouldBeNull();

		var every = await tray.GetAsync<RunningAppList>("/operator/debug/apps?every=true", cancellationToken);
		every.Every.ShouldBeTrue();
		every.Apps.Select(RunningApps.DisplayName).ShouldBe(["Rose Probe", "worker"]);

		var packages = await tray.GetAsync<InstalledAppList>("/operator/debug/packages", cancellationToken);
		packages.Apps.ShouldHaveSingleItem().AppUserModelId.ShouldBe(Installed.Probe.AppUserModelId);

		(await tray.GetAsync<RecentTargetList>("/operator/debug/recent", cancellationToken)).Targets.ShouldBeEmpty();
	}

	/// <summary>
	/// What was debugged lately is read from the per-machine file, newest first, and an entry of a kind this build does
	/// not know is left out rather than failing the list.
	/// </summary>
	[Test]
	public async Task Recent_targets_are_read_from_the_machines_file(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		RecentTargetsFile.Record(
			new RecentTargetRecord { Kind = "SomethingNewer", DisplayName = "From a later build", LastUsedUtc = DateTime.UtcNow }, home.Path);
		RecentTargetsFile.Record(
			new RecentTargetRecord
			{
				Kind = nameof(LiveAppTargetKind.LaunchExecutable),
				DisplayName = "InkLab.exe",
				ExecutablePath = @"D:\Tools\InkLab.exe",
				Arguments = "--sample strokes.json",
				LastUsedUtc = DateTime.UtcNow,
			},
			home.Path);

		await using var tray = await Tray.StartAsync(home, cancellationToken);

		var recent = await tray.GetAsync<RecentTargetList>("/operator/debug/recent", cancellationToken);

		var target = recent.Targets.ShouldHaveSingleItem();
		target.Kind.ShouldBe(LiveAppTargetKind.LaunchExecutable);
		target.Arguments.ShouldBe("--sample strokes.json");
	}

	/// <summary>
	/// Every start that cannot be made is refused as a bad request, saying why, before any host is started: a system
	/// process, the broker itself, a launch naming neither or both of a program and a package, a program that is not a
	/// full path, and an id that is not a packaged app's. None of them is a session.
	/// </summary>
	[Test]
	public async Task A_start_that_cannot_be_made_is_refused_saying_why(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);

		await RefusedAsync("/operator/debug/attach", new AttachRequest { ProcessId = 4 }, "system process");
		await RefusedAsync("/operator/debug/attach", new AttachRequest { ProcessId = Environment.ProcessId }, "this broker");
		await RefusedAsync("/operator/debug/launch", new LaunchRequest(), "names one thing to run");
		await RefusedAsync(
			"/operator/debug/launch",
			new LaunchRequest { ExecutablePath = Path.Combine(home.Path, "App.exe"), AppUserModelId = Installed.Probe.AppUserModelId },
			"names one thing to run");
		await RefusedAsync("/operator/debug/launch", new LaunchRequest { ExecutablePath = @"bin\App.exe" }, "is not a full path");
		await RefusedAsync("/operator/debug/launch", new LaunchRequest { AppUserModelId = "NotAnId" }, "is not an application user-model id");

		(await tray.GetAsync<OperatorSessionList>("/operator/sessions", cancellationToken)).Sessions.ShouldBeEmpty();
		(await tray.GetAsync<RecentTargetList>("/operator/debug/recent", cancellationToken)).Targets.ShouldBeEmpty("a refused start is not one to repeat");

		async Task RefusedAsync<T>(string route, T body, string reason)
		{
			using var response = await tray.Operator.PostAsJsonAsync(route, body, ContractJson.Options, cancellationToken);

			response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, route);
			(await response.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken))!.Message.ShouldContain(reason);
		}
	}

	/// <summary>Sharing or unsharing a session that is not open is not found, the way any read of it is.</summary>
	[Test]
	public async Task Sharing_a_session_that_is_not_open_is_not_found(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);

		using var share = await tray.Operator.PostAsJsonAsync(
			"/operator/sessions/session-none/share", new ShareRequest { Client = "client-0000" }, ContractJson.Options, cancellationToken);
		share.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using var unshare = await tray.Operator.DeleteAsync("/operator/sessions/session-none/share", cancellationToken);
		unshare.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// A share names a client the table lists, and never the one unnamed row, which stands for every client without a
	/// relay: sharing with it would hand the debugger to whichever of them called. A client that is named and listed
	/// goes on to the session, which here has ended.
	/// </summary>
	[Test]
	public async Task A_share_names_a_client_the_table_lists_by_its_relay(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);
		await using var agent = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions { Endpoint = tray.Endpoint }),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		await agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);
		await CancellableToolCall.InvokeAsync(
			agent, Echo.Name, new Dictionary<string, object?>(), progress: null, cancellationToken, session: CallSession.Mint());

		var clients = (await tray.GetAsync<ClientList>("/operator/clients", cancellationToken)).Clients;
		var unnamed = clients.Single(client => !client.Named);
		var named = clients.Single(client => client.Named);
		var debugging = tray.Services.GetRequiredService<OperatorDebugging>();

		(await Should.ThrowAsync<KeyNotFoundException>(
			() => debugging.ShareAsync("session-none", new ShareRequest { Client = "client-none" }, cancellationToken)))
			.Message.ShouldContain("lists no client client-none");

		(await Should.ThrowAsync<ArgumentException>(
			() => debugging.ShareAsync("session-none", new ShareRequest { Client = unnamed.Id }, cancellationToken)))
			.Message.ShouldContain("neither the relay nor a session of its own");

		(await Should.ThrowAsync<KeyNotFoundException>(
			() => debugging.ShareAsync("session-none", new ShareRequest { Client = named.Id }, cancellationToken)))
			.Message.ShouldContain("'session-none' ended");
	}

	/// <summary>A tool to call, so a client has a row in the table.</summary>
	[McpServerToolType]
	public sealed class Echo
	{
		public const string Name = "probe_debug_echo";

		[McpServerTool(Name = Name), Description("Answers at once.")]
		public static string Answer() => "echo";
	}

	/// <summary>The desktop as a test has it: one app with a window, and one .NET process without.</summary>
	private sealed class Running : IRunningApps
	{
		public IReadOnlyList<RunningApp> Read(bool every)
		{
			RunningApp[] apps =
			[
				new() { ProcessId = 22104, Name = "RoseProbe", WindowTitle = "Rose Probe", XamlStack = XamlStack.WinUi, HasWindow = true },
				new() { ProcessId = 22105, Name = "worker", HasWindow = false },
			];

			return [.. apps.Where(app => every || app.HasWindow)];
		}
	}

	/// <summary>One installed package, as a test has it.</summary>
	private sealed class Installed : IInstalledApps
	{
		public static readonly InstalledApp Probe = new()
		{
			AppUserModelId = "RoseProbe_8wekyb3d8bbwe!App",
			DisplayName = "Rose Probe",
			PackageFamilyName = "RoseProbe_8wekyb3d8bbwe",
			DevelopmentMode = true,
		};

		public IReadOnlyList<InstalledApp> Read() => [Probe];
	}

	/// <summary>A broker serving MCP and the operator API over http on a loopback port, as the tray does, reading a test's desktop.</summary>
	private sealed class Tray : IAsyncDisposable
	{
		private readonly WebApplication _app;

		private Tray(WebApplication app, OperatorToken token)
		{
			_app = app;

			Endpoint = new Uri(app.Urls.First() + "/");
			Anonymous = new HttpClient { BaseAddress = Endpoint };
			Operator = new HttpClient { BaseAddress = Endpoint };
			Operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
		}

		public Uri Endpoint { get; }

		public HttpClient Anonymous { get; }

		public HttpClient Operator { get; }

		public IServiceProvider Services => _app.Services;

		public static async Task<Tray> StartAsync(TemporaryLocalAppData home, CancellationToken cancellationToken)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Services.AddSingleton<IRunningApps, Running>();
			builder.Services.AddSingleton<IInstalledApps, Installed>();
			builder.Services
				.AddRoseMcpBroker(broker =>
				{
					broker.LocalAppData = home.Path;
					broker.ActivityDirectory = Path.Combine(home.Path, "Activity");
				})
				.WithTools<Echo>()
				.WithHttpTransport();

			var app = builder.Build();
			var token = OperatorToken.Mint();
			app.MapMcp();
			app.MapRoseAdminApi();
			app.MapRoseOperatorApi(token);

			await app.StartAsync(cancellationToken);

			return new Tray(app, token);
		}

		public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
			await Operator.GetFromJsonAsync<T>(path, ContractJson.Options, cancellationToken)
				?? throw new ShouldAssertException($"{path} answered nothing.");

		public async ValueTask DisposeAsync()
		{
			Anonymous.Dispose();
			Operator.Dispose();

			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
