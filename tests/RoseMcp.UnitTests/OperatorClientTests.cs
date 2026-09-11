using System.Net;
using System.Text;
using System.Text.Json;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The inspector's side of the operator API, against a handler that answers without a server.
/// <para>
/// What is worth testing here is not that HTTP works: it is the things that are silent when they
/// are wrong. A missing bearer header looks like a tray that will not talk. A kinds filter joined
/// with commas returns everything and reads as a filter that does nothing. A 401 reported as a
/// generic failure sends the reader to the logs instead of to the tray. And a caller cancelling
/// must not surface as the tray timing out, because a pane closing mid-request would then put a
/// complaint about the tray on screen on its way out.
/// </para>
/// </summary>
public sealed class OperatorClientTests
{
	private static readonly Uri Tray = new("http://127.0.0.1:5077");

	[Test]
	public async Task The_token_travels_as_a_bearer_header()
	{
		using var handler = new FakeHandler(_ => Json(new LiveBreakpointList()));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.BreakpointsAsync("s1", TestContext.Current!.Execution.CancellationToken);

		Assert.Equal("Bearer", handler.Last!.Headers.Authorization?.Scheme);
		Assert.Equal("tok-1", handler.Last.Headers.Authorization?.Parameter);
	}

	/// <summary>A client with no token sends none, rather than sending an empty one.</summary>
	[Test]
	public async Task No_token_means_no_header()
	{
		using var handler = new FakeHandler(_ => Json(new LiveBreakpointList()));
		using var client = new OperatorClient(Tray, null, handler);

		Assert.False(client.HasToken);
		await client.BreakpointsAsync("s1", TestContext.Current!.Execution.CancellationToken);

		Assert.Null(handler.Last!.Headers.Authorization);
	}

	/// <summary>
	/// Each kind is its own query parameter, because that is how ASP.NET binds a collection and a
	/// comma-joined list would arrive as one kind nothing matches -- a filter that silently returns
	/// nothing, which reads as a quiet target.
	/// </summary>
	[Test]
	public async Task The_events_query_repeats_each_kind()
	{
		using var handler = new FakeHandler(_ => Json(EmptyPage));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.EventsAsync(
			"s1", after: 12, kinds: ["BreakpointHit", "StepComplete"], limit: 50, waitSeconds: 30,
			TestContext.Current!.Execution.CancellationToken);

		var query = handler.Last!.RequestUri!.Query;
		Assert.Contains("after=12", query);
		Assert.Contains("limit=50", query);
		Assert.Contains("waitSeconds=30", query);
		Assert.Contains("kinds=BreakpointHit", query);
		Assert.Contains("kinds=StepComplete", query);
	}

	/// <summary>
	/// A long poll gets its wait plus the margin. Budgeted at the quick allowance instead, every
	/// thirty-second poll would be abandoned ten seconds in and the tail would quietly miss events.
	/// </summary>
	[Test]
	public void A_long_poll_is_budgeted_past_the_wait_it_asked_for()
	{
		Assert.True(OperatorClient.PollBudget(30) > TimeSpan.FromSeconds(30));
		Assert.Equal(TimeSpan.FromSeconds(30) + OperatorClient.PollMargin, OperatorClient.PollBudget(30));

		// A poll that waits for nothing still gets more than a quick call would need.
		Assert.Equal(OperatorClient.PollMargin, OperatorClient.PollBudget(0));
	}

	/// <summary>An enum in a request body goes as its name, which is what the host binds.</summary>
	[Test]
	public async Task A_request_body_carries_words_rather_than_numbers()
	{
		using var handler = new FakeHandler(_ => Json(new LiveContinueResult { Continued = true }));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.StepAsync("s1", "over", TestContext.Current!.Execution.CancellationToken);

		Assert.Equal("{\"mode\":\"over\"}", handler.LastBody);
	}

	/// <summary>A path's dots and brackets are escaped, since the grammar's separators are not the query's.</summary>
	[Test]
	public async Task A_value_path_is_escaped_into_the_query()
	{
		using var handler = new FakeHandler(_ => Json(new LiveValueExpansion
		{
			Execution = LiveExecutionState.StoppedAtBreakpoint,
			Path = "arg:0.Marks[1]",
			Total = 0,
			Truncated = false,
		}));

		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.ValueAsync("s1", "arg:0.Marks[1]", 0, null, TestContext.Current!.Execution.CancellationToken);

		Assert.Contains("path=arg%3A0.Marks%5B1%5D", handler.Last!.RequestUri!.Query);
	}

	[Test]
	public async Task A_refused_token_is_told_apart_and_says_what_to_do()
	{
		using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
		using var client = new OperatorClient(Tray, "stale", handler);

		var failure = await Assert.ThrowsAsync<OperatorException>(
			async () => await client.SessionsAsync(TestContext.Current!.Execution.CancellationToken));

		Assert.Equal(OperatorFailure.Unauthorized, failure.Failure);
		Assert.Equal(HttpStatusCode.Unauthorized, failure.Status);
		Assert.Equal(InspectorText.TokenRefused, failure.Message);
	}

	[Test]
	public async Task A_session_that_is_gone_is_told_apart()
	{
		using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var failure = await Assert.ThrowsAsync<OperatorException>(
			async () => await client.SessionAsync("gone", TestContext.Current!.Execution.CancellationToken));

		Assert.Equal(OperatorFailure.NotFound, failure.Failure);
	}

	/// <summary>
	/// The host's own sentence survives the trip, which is the whole point of the operator API
	/// forwarding real messages rather than statuses.
	/// </summary>
	[Test]
	public async Task A_refusal_carries_the_hosts_own_words()
	{
		using var handler = new FakeHandler(_ => Json(
			new OperatorError { Message = "Frame 9 is past the end of this thread's stack.", Status = 400 },
			HttpStatusCode.BadRequest));

		using var client = new OperatorClient(Tray, "tok-1", handler);

		var failure = await Assert.ThrowsAsync<OperatorException>(
			async () => await client.FrameVariablesAsync("s1", 9, null, TestContext.Current!.Execution.CancellationToken));

		Assert.Equal(OperatorFailure.Refused, failure.Failure);
		Assert.Equal("Frame 9 is past the end of this thread's stack.", failure.Message);
	}

	/// <summary>Nothing listening is its own outcome, and the message says where it looked.</summary>
	[Test]
	public async Task Nothing_listening_reads_as_no_tray()
	{
		using var handler = new FakeHandler(_ => throw new HttpRequestException("refused"));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var failure = await Assert.ThrowsAsync<OperatorException>(
			async () => await client.SessionsAsync(TestContext.Current!.Execution.CancellationToken));

		Assert.Equal(OperatorFailure.Unreachable, failure.Failure);
		Assert.Contains("127.0.0.1:5077", failure.Message);
	}

	/// <summary>
	/// A caller giving up is not the tray failing. A pane closing mid-request must not put "the
	/// tray did not answer" on screen on its way out.
	/// </summary>
	[Test]
	public async Task A_caller_that_gives_up_is_not_a_timeout()
	{
		using var handler = new FakeHandler(async (_, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		using var client = new OperatorClient(Tray, "tok-1", handler);
		using var abandoned = new CancellationTokenSource();

		var reading = client.SessionsAsync(abandoned.Token);
		await abandoned.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
		Assert.False(reading.IsFaulted, "a caller's own cancellation is a cancellation, not a fault");
	}

	private static readonly LiveDebugEventPage EmptyPage = new()
	{
		State = LiveAppSessionState.Ready,
		NextCursor = 0,
		OldestAvailable = 0,
		TotalObserved = 0,
	};

	private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
	{
		Content = new StringContent(
			JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json"),
	};

	/// <summary>
	/// A handler that answers from a function and remembers what it was asked, so a test can assert
	/// on the request as well as on what the client made of the reply.
	/// </summary>
	private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
		: HttpMessageHandler
	{
		public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
			: this((request, _) => Task.FromResult(answer(request)))
		{
		}

		/// <summary>The last request, kept because a URI composed wrongly is the quiet failure here.</summary>
		public HttpRequestMessage? Last { get; private set; }

		/// <summary>Its body as sent, for asserting how a request record serialises.</summary>
		public string? LastBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Last = request;
			LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

			return await answer(request, cancellationToken);
		}
	}
}
