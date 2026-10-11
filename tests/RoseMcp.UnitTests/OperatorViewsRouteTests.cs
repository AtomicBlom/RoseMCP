using System.ComponentModel;
using System.Diagnostics;
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

namespace RoseMcp.UnitTests;

/// <summary>
/// The views a person's windows read at <c>/operator</c>, over a real broker on a loopback port: each behind the
/// token, each answering at once with its version, and each that changes held open until it does -- answered as
/// soon as a call changes it, and when the host stops, rather than holding the stop up.
/// </summary>
public sealed class OperatorViewsRouteTests : IDisposable
{
	private static readonly TimeSpan Soon = TimeSpan.FromSeconds(15);

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "rose-operator-views", Guid.NewGuid().ToString("N"));

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
	[Arguments("/operator/clients")]
	[Arguments("/operator/workspaces")]
	[Arguments("/operator/sessions")]
	[Arguments("/operator/activity")]
	[Arguments("/operator/memory")]
	public async Task Every_view_needs_the_operator_token(string route, CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);

		(await tray.Anonymous.GetAsync(route, cancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await tray.Operator.GetAsync(route, cancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	/// <summary>
	/// A broker nobody has asked to load anything holds no workspace and no debug session, and says what it would
	/// close an idle workspace after; every view carries a version, and reading one twice gives the same.
	/// </summary>
	[Test]
	public async Task An_idle_broker_lists_nothing_and_each_view_has_a_steady_version(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);

		var workspaces = await tray.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken);
		workspaces.Workspaces.ShouldBeEmpty();
		workspaces.IdleEvictionAfter.ShouldBe(TimeSpan.FromMinutes(45));
		workspaces.Version.ShouldNotBeNullOrWhiteSpace();
		(await tray.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken)).Version.ShouldBe(workspaces.Version);

		var sessions = await tray.GetAsync<OperatorSessionList>("/operator/sessions", cancellationToken);
		sessions.Sessions.ShouldBeEmpty();
		(await tray.GetAsync<OperatorSessionList>("/operator/sessions", cancellationToken)).Version.ShouldBe(sessions.Version);

		var clients = await tray.GetAsync<ClientList>("/operator/clients", cancellationToken);
		clients.Clients.ShouldBeEmpty();
		(await tray.GetAsync<ClientList>("/operator/clients", cancellationToken)).Version.ShouldBe(clients.Version);
	}

	/// <summary>The broker's own process, and the machine, are read on every request; no worker or debug session means none listed.</summary>
	[Test]
	public async Task Memory_names_the_brokers_process_and_the_machine(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);

		var memory = await tray.GetAsync<MemoryReport>("/operator/memory", cancellationToken);

		memory.Broker.ProcessId.ShouldBe(Environment.ProcessId, "the test hosts the broker, as the tray does");
		memory.Broker.WorkingSetBytes.ShouldBeGreaterThan(0);
		memory.Broker.ManagedHeapBytes.ShouldNotBeNull().ShouldBeGreaterThan(0);
		memory.Workers.ShouldBeEmpty();
		memory.LiveApps.ShouldBeEmpty();

		var machine = memory.Machine.ShouldNotBeNull();
		machine.TotalBytes.ShouldBeGreaterThan(memory.Broker.WorkingSetBytes);
		if (machine.AvailableBytes is { } available) available.ShouldBeLessThanOrEqualTo(machine.TotalBytes);
	}

	/// <summary>
	/// A window holding the client table and the activity record open is answered as soon as an agent calls,
	/// rather than at the end of its wait; and a wait on a view nothing changes runs to its end with the same view.
	/// </summary>
	[Test]
	public async Task A_call_answers_the_windows_waiting_on_the_clients_and_the_activity(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);
		await using var agent = await tray.DirectAsync(cancellationToken);

		await agent.ListToolsAsync(cancellationToken: cancellationToken);
		var clients = await tray.GetAsync<ClientList>("/operator/clients", cancellationToken);
		var activity = await tray.GetAsync<ActivityPage>($"/operator/activity?tool={Echo.Name}", cancellationToken);
		activity.Entries.ShouldBeEmpty();

		var clientsWait = tray.GetAsync<ClientList>($"/operator/clients?after={clients.Version}&waitSeconds=60", cancellationToken);
		var activityWait = tray.GetAsync<ActivityPage>(
			$"/operator/activity?tool={Echo.Name}&after={activity.Version}&waitSeconds=60", cancellationToken);

		await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		clientsWait.IsCompleted.ShouldBeFalse("nothing has called yet");
		activityWait.IsCompleted.ShouldBeFalse();

		await agent.CallToolAsync(Echo.Name, new Dictionary<string, object?> { ["text"] = "hello" }, cancellationToken: cancellationToken);

		var changedClients = await clientsWait.WaitAsync(Soon, cancellationToken);
		changedClients.Version.ShouldNotBe(clients.Version);
		changedClients.Clients.ShouldHaveSingleItem().Calls.ShouldBe(1);

		var changedActivity = await activityWait.WaitAsync(Soon, cancellationToken);
		changedActivity.Entries.ShouldHaveSingleItem().Tool.ShouldBe(Echo.Name);
		changedActivity.Version.ShouldNotBe(activity.Version);

		// A view read without its bodies and with them carries one version, so a window can wait on either.
		await tray.Record.FlushAsync(cancellationToken);
		var settled = await tray.GetAsync<ActivityPage>($"/operator/activity?tool={Echo.Name}", cancellationToken);
		var withBodies = await tray.GetAsync<ActivityPage>($"/operator/activity?tool={Echo.Name}&bodies=true", cancellationToken);
		withBodies.Entries.ShouldHaveSingleItem().Response.ShouldBe("echo: hello");
		withBodies.Version.ShouldBe(settled.Version);

		var unchanged = await tray.GetAsync<ActivityPage>(
			$"/operator/activity?tool={Echo.Name}&bodies=true&after={settled.Version}&waitSeconds=1", cancellationToken);
		unchanged.Version.ShouldBe(settled.Version, "nothing changed, so the wait ran out and answered the same view");
		unchanged.Entries.ShouldHaveSingleItem().Response.ShouldBe("echo: hello");
	}

	/// <summary>The activity route reads only the failures, or what names some text, a page at a time; a cursor it never gave is refused saying so.</summary>
	[Test]
	public async Task The_activity_route_reads_failures_text_and_pages(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);
		await using var agent = await tray.DirectAsync(cancellationToken);

		for (var call = 1; call <= 3; call++)
		{
			await agent.CallToolAsync(Echo.Name, new Dictionary<string, object?> { ["text"] = $"call {call}" }, cancellationToken: cancellationToken);
		}

		(await agent.CallToolAsync(Failing.Name, cancellationToken: cancellationToken)).IsError.ShouldBe(true);
		await tray.Record.FlushAsync(cancellationToken);

		var failures = await tray.GetAsync<ActivityPage>("/operator/activity?failures=true", cancellationToken);
		failures.Entries.ShouldHaveSingleItem().Tool.ShouldBe(Failing.Name);

		var found = await tray.GetAsync<ActivityPage>("/operator/activity?text=CART%20IS%20EMPTY", cancellationToken);
		found.Entries.ShouldHaveSingleItem().Tool.ShouldBe(Failing.Name);

		var first = await tray.GetAsync<ActivityPage>($"/operator/activity?tool={Echo.Name}&limit=2", cancellationToken);
		first.Entries.Count.ShouldBe(2);
		first.Next.ShouldNotBeNull();

		var rest = await tray.GetAsync<ActivityPage>(
			$"/operator/activity?tool={Echo.Name}&limit=2&before={Uri.EscapeDataString(first.Next)}", cancellationToken);
		rest.Entries.ShouldHaveSingleItem();
		rest.Next.ShouldBeNull();
		first.Entries.Concat(rest.Entries).Select(entry => entry.Id).Distinct().Count().ShouldBe(3);

		var refused = await tray.Operator.GetAsync("/operator/activity?before=yesterday", cancellationToken);
		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await refused.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken))
			.ShouldNotBeNull().Message.ShouldContain("is not a cursor the activity record gave", Case.Sensitive);
	}

	/// <summary>
	/// A host stopping answers every wait still open, so a window's poll never holds a stop -- a deploy, a tray
	/// exiting -- for the rest of its minute.
	/// </summary>
	[Test]
	public async Task The_host_stopping_answers_a_wait_still_open(CancellationToken cancellationToken)
	{
		await using var tray = await Tray.StartAsync(_directory, cancellationToken);

		var workspaces = await tray.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken);
		var waiting = tray.Operator.GetAsync($"/operator/workspaces?after={workspaces.Version}&waitSeconds=60", cancellationToken);

		await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		waiting.IsCompleted.ShouldBeFalse();

		var stopping = Stopwatch.StartNew();
		await tray.StopAsync().WaitAsync(Soon, cancellationToken);
		stopping.Elapsed.ShouldBeLessThan(Soon, "the stop was not held for the poll's minute");

		using var answer = await waiting.WaitAsync(Soon, cancellationToken);
		answer.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await answer.Content.ReadFromJsonAsync<OperatorWorkspaceList>(ContractJson.Options, cancellationToken))
			.ShouldNotBeNull().Version.ShouldBe(workspaces.Version);
	}

	/// <summary>Tools to call, standing in for the broker's own, which need a worker this suite does not start.</summary>
	[McpServerToolType]
	public sealed class Echo
	{
		public const string Name = "probe_view_echo";

		[McpServerTool(Name = Name), Description("Answers at once.")]
		public static string Answer(string? text = null) => $"echo: {text}";
	}

	[McpServerToolType]
	public sealed class Failing
	{
		public const string Name = "probe_view_fail";

		[McpServerTool(Name = Name), Description("Always fails.")]
		public static string Answer() => throw new InvalidOperationException("The cart is empty.");
	}

	/// <summary>A broker serving MCP, the admin routes and the operator routes over http on a loopback port, as the tray does.</summary>
	private sealed class Tray : IAsyncDisposable
	{
		private readonly WebApplication _app;
		private bool _stopped;

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
				.AddRoseMcpBroker(broker =>
				{
					broker.ActivityDirectory = directory;
					broker.IdleEvictionAfter = TimeSpan.FromMinutes(45);
				})
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

		/// <summary>A client of this broker with no relay in front of it.</summary>
		public async Task<McpClient> DirectAsync(CancellationToken cancellationToken) =>
			await McpClient.CreateAsync(
				new HttpClientTransport(new HttpClientTransportOptions { Endpoint = Endpoint }),
				new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
				cancellationToken: cancellationToken);

		public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
			await Operator.GetFromJsonAsync<T>(path, ContractJson.Options, cancellationToken)
				?? throw new ShouldAssertException($"{path} answered nothing.");

		public async Task StopAsync()
		{
			_stopped = true;
			await _app.StopAsync();
		}

		public async ValueTask DisposeAsync()
		{
			Anonymous.Dispose();
			Operator.Dispose();

			if (!_stopped) await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
