using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using RoseMcp.Contracts;

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

		Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
	}

	[Test]
	public async Task Refuses_a_request_with_the_wrong_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var request = new HttpRequestMessage(HttpMethod.Get, broker.Url("/operator/sessions"));
		request.Headers.Add("Authorization", "Bearer not-the-token");

		using var answer = await broker.Anonymous.SendAsync(request, cancellationToken);

		Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
	}

	[Test]
	public async Task Lists_no_sessions_on_a_broker_nobody_is_debugging_through()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		var sessions = await broker.Client.GetFromJsonAsync<LiveAppSessionSummary[]>(
			broker.Url("/operator/sessions"), ContractJson.Options, cancellationToken);

		Assert.NotNull(sessions);
		Assert.Empty(sessions!);
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

		Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);

		var error = await answer.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken);

		Assert.NotNull(error);
		Assert.Contains("session-nothere", error!.Message);
		Assert.Contains("/operator/sessions", error.Message);
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
	[Category("LiveApp")]
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
				cancellationToken: cancellationToken);

			var attached = await agent.CallToolAsync(
				ToolNames.DebugAttach,
				new Dictionary<string, object?> { ["processId"] = target.Id },
				cancellationToken: cancellationToken);

			var sessionId = attached.StructuredContent?.Deserialize<LiveAppSessionSummary>(ContractJson.Options)?.SessionId;
			Assert.NotNull(sessionId);

			var listed = await broker.Client.GetFromJsonAsync<LiveAppSessionSummary[]>(
				broker.Url("/operator/sessions"), ContractJson.Options, cancellationToken);

			Assert.NotNull(listed);
			Assert.Contains(listed!, summary => summary.SessionId == sessionId);

			// And reachable one at a time, which is what every other route depends on.
			using var one = await broker.Client.GetAsync(broker.Url($"/operator/sessions/{sessionId}"), cancellationToken);
			Assert.Equal(HttpStatusCode.OK, one.StatusCode);

			// Detached from here too, leaving the target running: an operator holds the whole machine's
			// sessions, so ending one is theirs to do.
			using var detached = await broker.Client.PostAsync(
				broker.Url($"/operator/sessions/{sessionId}/detach"), content: null, cancellationToken);

			Assert.Equal(HttpStatusCode.OK, detached.StatusCode);

			var closed = await detached.Content.ReadFromJsonAsync<LiveSessionClosed>(
				ContractJson.Options, cancellationToken);

			Assert.NotNull(closed);
			Assert.True(closed!.Closed, "the operator path closes a session an agent started");
			Assert.False(target.HasExited, "a detach leaves the target running");

			// Gone by name, which is the other half of a close.
			using var again = await broker.Client.GetAsync(broker.Url($"/operator/sessions/{sessionId}"), cancellationToken);
			Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
		}
		finally
		{
			if (!target.HasExited) target.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A dedicated child process to attach to, rather than this test runner: attaching a debugger to
	/// the process running the test perturbs it, and the probe does nothing but throw a distinctively
	/// named exception on a loop.
	/// </summary>
	private static Process StartProbeTarget()
	{
		var executable = Path.Combine(
			TestToolchain.RepositoryRoot(),
			"tests",
			"DebugProbeTarget",
			"bin",
			TestToolchain.Configuration(),
			"net10.0",
			OperatingSystem.IsWindows() ? "DebugProbeTarget.exe" : "DebugProbeTarget");

		if (!File.Exists(executable))
		{
			throw new FileNotFoundException($"The probe target has not been built at {executable}.", executable);
		}

		return Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
			?? throw new InvalidOperationException("Could not start the probe target.");
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
