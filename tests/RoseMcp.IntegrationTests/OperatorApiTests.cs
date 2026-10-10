using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using RoseMcp.Contracts;
using RoseMcp.Logging;
using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The http surface a person's tools read a broker through, over a real server process.
/// <para>
/// A real process because the thing being tested is the arrangement rather than the handlers: which
/// requests the token refuses, and -- the point of the whole surface -- that a session an MCP client
/// started is visible here. An in-process host would exercise the handlers and answer neither, since
/// both turn on how a request arrives.
/// </para>
/// <para>
/// The token is chosen rather than discovered. A server with no <c>ROSEMCP_TOKEN</c> mints one and
/// says so only in its log, which is the right behaviour and no use to a test.
/// </para>
/// </summary>
public sealed class OperatorApiTests
{
	private const string Token = "a-token-only-this-test-knows";

	/// <summary>
	/// Long enough for a cold server to bind on a loaded machine, and bounded: a fixture check that
	/// can hang is worse than the bug it looks for.
	/// </summary>
	private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

	[Test]
	public async Task Refuses_a_request_with_no_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var answer = await broker.Anonymous.GetAsync(broker.Url("/operator/sessions"), cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Refuses_a_request_with_the_wrong_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var request = new HttpRequestMessage(HttpMethod.Get, broker.Url("/operator/sessions"));
		request.Headers.Add("Authorization", "Bearer not-the-token");

		using var answer = await broker.Anonymous.SendAsync(request, cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// Hello names the broker's build by its commit, which is this suite's own: the server was built
	/// from the same tree in the same build. For a local build it also counts how far the checkout has
	/// moved on, which git can always do for the commit the checkout was built at.
	/// </summary>
	[Test]
	public async Task Hello_names_the_brokers_build_by_its_commit()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		var hello = await broker.Client.GetFromJsonAsync<OperatorHello>(
			broker.Url("/operator/hello"), ContractJson.Options, cancellationToken);

		var ours = BuildIdentity.Of(typeof(OperatorApiTests).Assembly);

		hello.ShouldNotBeNull();
		hello!.Build.Commit.ShouldNotBeNull();
		hello.Build.Commit.ShouldBe(ours.Commit);
		hello.Build.BuiltUtc.ShouldNotBeNull();
		hello.Build.Checkout.ShouldBe(ours.Checkout);

		if (ours.Checkout is null)
		{
			hello.Checkout.ShouldBeNull("a build that names no checkout has nothing to count from");
			return;
		}

		hello.Checkout.ShouldNotBeNull();
		hello.Checkout!.Head.Count.ShouldNotBeNull(hello.Checkout.Head.Unknown);
	}

	/// <summary>Hello is behind the token like everything else here: a build's checkout path is the machine's business.</summary>
	[Test]
	public async Task Hello_needs_the_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var answer = await broker.Anonymous.GetAsync(broker.Url("/operator/hello"), cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Lists_no_sessions_on_a_broker_nobody_is_debugging_through()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		var sessions = await broker.Client.GetFromJsonAsync<LiveAppSessionSummary[]>(
			broker.Url("/operator/sessions"), ContractJson.Options, cancellationToken);

		sessions.ShouldNotBeNull();
		sessions!.ShouldBeEmpty();
	}

	/// <summary>
	/// A session that is not there is a 404 carrying the sentence that says what to do about it, not
	/// a bare status. The message is the contract: a caller told only that something failed has to
	/// guess, and the guesses are expensive.
	/// </summary>
	[Test]
	public async Task Says_which_sessions_there_are_when_asked_for_one_that_is_gone()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var answer = await broker.Client.GetAsync(
			broker.Url("/operator/sessions/session-nothere"), cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		var error = await answer.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken);

		error.ShouldNotBeNull();
		error!.Message.ShouldContain("session-nothere", Case.Sensitive);
		error.Message.ShouldContain("/operator/sessions", Case.Sensitive);
	}

	/// <summary>
	/// The whole reason this surface exists. A session started by an MCP client belongs to that
	/// client, and every agent-facing debug tool refuses it to anyone else -- which is right, and
	/// which would make a window showing sessions impossible, because inside an http endpoint there
	/// is no MCP session to be the owner. So the operator path resolves sessions without the
	/// ownership check, and this is what says it does.
	/// <para>
	/// The session is started through a real http MCP client, which is what an agent talking to a
	/// tray is. That matters: it gives the session a transport session id as its owner, and a null
	/// owner would match what an endpoint sees and prove nothing.
	/// </para>
	/// </summary>
	[Test]
	public async Task Shows_a_session_an_mcp_client_started_and_owns()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var target = StartProbeTarget();
		try
		{
			await using var agent = await McpClient.CreateAsync(
				new HttpClientTransport(new HttpClientTransportOptions
				{
					Endpoint = new Uri(broker.Url("/")),
					AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
				}),

				// A server/discover probe the client gives up on itself falls back to initialize with the probe's
				// protocol version still in the http header, and the broker refuses the mismatch -- which a full
				// suite's load reaches inside the probe's five seconds. The broker's own children are held to the
				// same rule.
				new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
				cancellationToken: cancellationToken);

			var attached = await agent.CallToolAsync(
				ToolNames.DebugAttach,
				new Dictionary<string, object?> { ["processId"] = target.Id },
				cancellationToken: cancellationToken);

			var sessionId = attached.StructuredContent?.Deserialize<LiveAppSessionSummary>(ContractJson.Options)?.SessionId;
			sessionId.ShouldNotBeNull();

			var listed = await broker.Client.GetFromJsonAsync<LiveAppSessionSummary[]>(
				broker.Url("/operator/sessions"), ContractJson.Options, cancellationToken);

			listed.ShouldNotBeNull();
			listed!.ShouldContain(summary => summary.SessionId == sessionId);

			// And reachable one at a time, which is what every other route depends on.
			using var one = await broker.Client.GetAsync(broker.Url($"/operator/sessions/{sessionId}"), cancellationToken);
			one.StatusCode.ShouldBe(HttpStatusCode.OK);

			// Detached from here too, leaving the target running: an operator holds the whole machine's
			// sessions, so ending one is theirs to do.
			using var detached = await broker.Client.PostAsync(
				broker.Url($"/operator/sessions/{sessionId}/detach"), content: null, cancellationToken);

			detached.StatusCode.ShouldBe(HttpStatusCode.OK);

			var closed = await detached.Content.ReadFromJsonAsync<LiveSessionClosed>(
				ContractJson.Options, cancellationToken);

			closed.ShouldNotBeNull();
			closed!.Closed.ShouldBeTrue("the operator path closes a session an agent started");
			target.HasExited.ShouldBeFalse("a detach leaves the target running");

			// Gone by name, which is the other half of a close.
			using var again = await broker.Client.GetAsync(broker.Url($"/operator/sessions/{sessionId}"), cancellationToken);
			again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}
		finally
		{
			if (!target.HasExited) target.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// An operator request is a call entering Rose, so it is given an id the way a tool call is, and the
	/// hop it makes to the live-app host carries it. Found from the host's side: the line the host writes
	/// for the detach names an id, and that id is on the broker's lines too -- which it is only if the
	/// broker minted it and sent it, since a host sent none mints one nobody else has.
	/// </summary>
	[Test]
	public async Task An_operator_request_carries_one_id_to_the_host_it_reaches()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var target = StartProbeTarget();
		try
		{
			await using var agent = await McpClient.CreateAsync(
				new HttpClientTransport(new HttpClientTransportOptions
				{
					Endpoint = new Uri(broker.Url("/")),
					AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
				}),
				new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
				cancellationToken: cancellationToken);

			var attached = await agent.CallToolAsync(
				ToolNames.DebugAttach,
				new Dictionary<string, object?> { ["processId"] = target.Id },
				cancellationToken: cancellationToken);

			var summary = attached.StructuredContent?.Deserialize<LiveAppSessionSummary>(ContractJson.Options);
			summary.ShouldNotBeNull();
			var hostLog = summary!.HostLogPath;
			hostLog.ShouldNotBeNull("the host names the log it writes");

			using var detached = await broker.Client.PostAsync(
				broker.Url($"/operator/sessions/{summary.SessionId}/detach"), content: null, cancellationToken);
			detached.StatusCode.ShouldBe(HttpStatusCode.OK);

			var detachLine = Lines(hostLog)
			.Where(line => line.Contains($"Detached from pid {target.Id}", StringComparison.Ordinal))
			.ToList()
			.ShouldHaveSingleItem();
			var id = detachLine.Split(' ')[3];

			CallCorrelation.IsWellFormed(id).ShouldBeTrue($"the host's detach line names no call: {detachLine}");

			var serverLogs = Directory.GetFiles(
				Path.Combine(Environment.GetEnvironmentVariable(RoseLogFile.RootVariable)!, "Server"), "*.log");

			serverLogs.SelectMany(Lines).ShouldContain(
				line => line.Contains($"] {id} ", StringComparison.Ordinal),
				$"no broker line carries {id}, so the host minted it rather than being sent it");
		}
		finally
		{
			if (!target.HasExited) target.Kill(entireProcessTree: true);
		}
	}

	/// <summary>Reads a log another process still holds open for writing.</summary>
	private static IEnumerable<string> Lines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);

		return reader.ReadToEnd().Split('\n');
	}

	/// <summary>
	/// An http broker whose operator token this test chose, with clients for both the authorised and
	/// the anonymous case.
	/// </summary>
	private sealed class OperatorBroker : IAsyncDisposable
	{
		private readonly RoseServerProcess _process;

		private OperatorBroker(RoseServerProcess process, int port)
		{
			_process = process;
			Port = port;

			Client = new HttpClient { Timeout = Ceiling };
			Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {Token}");

			Anonymous = new HttpClient { Timeout = Ceiling };
		}

		public int Port { get; }

		/// <summary>Carries the token, so every request is one an operator's tools would make.</summary>
		public HttpClient Client { get; }

		/// <summary>Carries nothing, which is what any other local process would be.</summary>
		public HttpClient Anonymous { get; }

		public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

		public static async Task<OperatorBroker> StartAsync(CancellationToken cancellationToken)
		{
			var port = RoseServerProcess.FreePort();
			var process = RoseServerProcess.StartWith(
				new Dictionary<string, string> { ["ROSEMCP_TOKEN"] = Token },
				"--transport",
				"http",
				"--port",
				port.ToString());

			var broker = new OperatorBroker(process, port);

			try
			{
				await broker.WaitUntilListeningAsync(cancellationToken);
				return broker;
			}
			catch
			{
				await broker.DisposeAsync();
				throw;
			}
		}

		/// <summary>
		/// Waits for the server to answer, bounded. An unbounded wait here turns a server that failed
		/// to bind into a wedged suite rather than a failing test.
		/// </summary>
		private async Task WaitUntilListeningAsync(CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + Ceiling;

			while (DateTime.UtcNow < deadline)
			{
				if (_process.HasExited) throw new InvalidOperationException("The broker exited before it answered.");

				try
				{
					using var answer = await Client.GetAsync(Url("/admin/workspaces"), cancellationToken);
					if (answer.IsSuccessStatusCode) return;
				}
				catch (HttpRequestException)
				{
					// Not up yet.
				}

				await Task.Delay(100, cancellationToken);
			}

			throw new InvalidOperationException($"The broker did not answer on port {Port} within {Ceiling.TotalSeconds:0}s.");
		}

		public ValueTask DisposeAsync()
		{
			Client.Dispose();
			Anonymous.Dispose();
			_process.Dispose();

			return ValueTask.CompletedTask;
		}
	}
}
