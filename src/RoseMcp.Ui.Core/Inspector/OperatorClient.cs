using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// The inspector's side of the operator API: one typed method per route, and one place that turns
/// what went wrong into something a window can say.
/// <para>
/// The budget is per request rather than on the client, because the calls differ by two orders of
/// magnitude. A session list should answer in milliseconds and a ten-second wait for one means the
/// tray is wedged; a XAML tree read legitimately takes half a minute against a busy app; a long
/// poll is meant to take as long as it was asked to. One <c>HttpClient.Timeout</c> covering all
/// three would either abandon the tree read or let a hung status call sit there looking healthy.
/// </para>
/// <para>
/// A caller giving up and a budget expiring are told apart, and the difference is not cosmetic: a
/// pane that was closed mid-request must not put "the tray timed out" on screen on its way out.
/// </para>
/// </summary>
public sealed class OperatorClient : IDisposable
{
	/// <summary>What a call that only reads state gets. Longer than any of them should ever need.</summary>
	public static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

	/// <summary>
	/// What a XAML call gets. The host itself waits twenty seconds for an injection to be sited,
	/// so anything shorter would give up on the case the wait exists for.
	/// </summary>
	public static readonly TimeSpan Xaml = TimeSpan.FromSeconds(45);

	/// <summary>
	/// What a long poll gets beyond the wait it asked for. Enough for the round trip and the host's
	/// own bookkeeping, and no more: a poll that outlives its wait by much is a poll that is stuck.
	/// </summary>
	public static readonly TimeSpan PollMargin = TimeSpan.FromSeconds(15);

	/// <summary>
	/// How long a long poll is given: the wait it asked for, plus the margin.
	/// <para>
	/// Named rather than inlined because getting it wrong is invisible until it is infuriating. A
	/// poll budgeted at <see cref="Quick"/> abandons every thirty-second wait ten seconds in, and
	/// the symptom is an event tail that looks like it is working and misses things.
	/// </para>
	/// </summary>
	public static TimeSpan PollBudget(int waitSeconds) => TimeSpan.FromSeconds(Math.Max(waitSeconds, 0)) + PollMargin;

	private readonly HttpClient _http;

	/// <param name="baseAddress">Where the tray is listening.</param>
	/// <param name="token">The tray's operator token, or null when there is none to present.</param>
	/// <param name="handler">
	/// A handler to use instead of the default, for a test. It is not disposed with this client,
	/// so one handler can serve several.
	/// </param>
	public OperatorClient(Uri baseAddress, string? token, HttpMessageHandler? handler = null)
	{
		_http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
		_http.BaseAddress = baseAddress;

		// Infinite here, bounded per request. The two are not interchangeable: HttpClient.Timeout
		// cancels with a TaskCanceledException indistinguishable from a caller's own cancellation,
		// and telling those apart is the whole reason the budget is imposed with a linked token.
		_http.Timeout = Timeout.InfiniteTimeSpan;

		if (!string.IsNullOrWhiteSpace(token))
		{
			_http.DefaultRequestHeaders.Authorization = new("Bearer", token);
		}

		BaseAddress = baseAddress;
		HasToken = !string.IsNullOrWhiteSpace(token);
	}

	public Uri BaseAddress { get; }

	/// <summary>Whether this client has a token at all, which decides which empty state to show.</summary>
	public bool HasToken { get; }

	public Task<IReadOnlyList<LiveAppSessionSummary>> SessionsAsync(CancellationToken cancellationToken) =>
		GetAsync<IReadOnlyList<LiveAppSessionSummary>>("/operator/sessions", Quick, cancellationToken);

	public Task<LiveAppSessionSummary> SessionAsync(string sessionId, CancellationToken cancellationToken) =>
		GetAsync<LiveAppSessionSummary>($"/operator/sessions/{sessionId}", Quick, cancellationToken);

	/// <summary>
	/// A page of the event tail. <paramref name="waitSeconds"/> above zero makes it a long poll,
	/// which the host holds open until an event arrives -- so the budget is the wait plus a margin.
	/// </summary>
	public Task<LiveDebugEventPage> EventsAsync(
		string sessionId,
		long after,
		IReadOnlyList<string>? kinds,
		int limit,
		int waitSeconds,
		CancellationToken cancellationToken)
	{
		var query = new List<string>
		{
			$"after={after}",
			$"limit={limit}",
			$"waitSeconds={waitSeconds}",
		};

		// Repeated rather than comma-joined, because that is how ASP.NET binds a collection from a
		// query string and a comma inside one value would otherwise split it.
		foreach (var kind in kinds ?? []) query.Add($"kinds={Uri.EscapeDataString(kind)}");

		return GetAsync<LiveDebugEventPage>(
			$"/operator/sessions/{sessionId}/events?{string.Join('&', query)}",
			PollBudget(waitSeconds),
			cancellationToken);
	}

	public Task<LiveBreakpointList> BreakpointsAsync(string sessionId, CancellationToken cancellationToken) =>
		GetAsync<LiveBreakpointList>($"/operator/sessions/{sessionId}/breakpoints", Quick, cancellationToken);

	public Task<LiveBreakpoint> SetBreakpointAsync(string sessionId, SetBreakpointRequest request, CancellationToken cancellationToken) =>
		PostAsync<SetBreakpointRequest, LiveBreakpoint>(
			$"/operator/sessions/{sessionId}/breakpoints", request, Quick, cancellationToken);

	public Task<LiveBreakpointList> RemoveBreakpointAsync(string sessionId, string breakpointId, CancellationToken cancellationToken) =>
		SendAsync<LiveBreakpointList>(
			() => new HttpRequestMessage(HttpMethod.Delete, $"/operator/sessions/{sessionId}/breakpoints/{breakpointId}"),
			Quick,
			cancellationToken);

	public Task<LiveTracepointList> TracepointsAsync(string sessionId, CancellationToken cancellationToken) =>
		GetAsync<LiveTracepointList>($"/operator/sessions/{sessionId}/tracepoints", Quick, cancellationToken);

	public Task<LiveTracepoint> AddTracepointAsync(string sessionId, AddTracepointRequest request, CancellationToken cancellationToken) =>
		PostAsync<AddTracepointRequest, LiveTracepoint>(
			$"/operator/sessions/{sessionId}/tracepoints", request, Quick, cancellationToken);

	public Task<LiveTracepointList> RemoveTracepointAsync(string sessionId, string tracepointId, CancellationToken cancellationToken) =>
		SendAsync<LiveTracepointList>(
			() => new HttpRequestMessage(HttpMethod.Delete, $"/operator/sessions/{sessionId}/tracepoints/{tracepointId}"),
			Quick,
			cancellationToken);

	public Task<LiveContinueResult> ContinueAsync(string sessionId, CancellationToken cancellationToken) =>
		PostAsync<object?, LiveContinueResult>($"/operator/sessions/{sessionId}/continue", null, Quick, cancellationToken);

	public Task<LiveContinueResult> StepAsync(string sessionId, string mode, CancellationToken cancellationToken) =>
		PostAsync<StepRequest, LiveContinueResult>(
			$"/operator/sessions/{sessionId}/step", new StepRequest { Mode = mode }, Quick, cancellationToken);

	public Task<LiveHoldResult> HoldAsync(string sessionId, int? seconds, bool release, CancellationToken cancellationToken) =>
		PostAsync<HoldRequest, LiveHoldResult>(
			$"/operator/sessions/{sessionId}/hold",
			new HoldRequest { Seconds = seconds, Release = release },
			Quick,
			cancellationToken);

	/// <summary>
	/// Stops a running target where it stands. Given the XAML budget rather than the quick one: the
	/// reason somebody reaches for pause is an app that is busy or wedged, which is exactly when the
	/// runtime takes its time reaching a point it can be stopped at.
	/// </summary>
	public Task<LivePauseResult> BreakAsync(string sessionId, CancellationToken cancellationToken) =>
		PostAsync<object?, LivePauseResult>($"/operator/sessions/{sessionId}/break", null, Xaml, cancellationToken);

	public Task<LiveStackFrames> FramesAsync(
		string sessionId,
		int? threadId,
		int offset,
		int? limit,
		CancellationToken cancellationToken)
	{
		var query = new List<string> { $"offset={offset}" };
		if (threadId is { } thread) query.Add($"threadId={thread}");
		if (limit is { } page) query.Add($"limit={page}");

		return GetAsync<LiveStackFrames>(
			$"/operator/sessions/{sessionId}/frames?{string.Join('&', query)}", Quick, cancellationToken);
	}

	public Task<LiveFrameVariables> FrameVariablesAsync(
		string sessionId,
		int frameIndex,
		int? threadId,
		CancellationToken cancellationToken)
	{
		var query = threadId is { } thread ? $"?threadId={thread}" : string.Empty;

		return GetAsync<LiveFrameVariables>(
			$"/operator/sessions/{sessionId}/frames/{frameIndex}/variables{query}", Quick, cancellationToken);
	}

	public Task<LiveValueExpansion> ValueAsync(
		string sessionId,
		string path,
		int frameIndex,
		int? threadId,
		CancellationToken cancellationToken)
	{
		// Escaped, because a path carries dots and brackets and can carry a field name with
		// anything in it: the grammar's separators are not the query string's.
		var query = new List<string> { $"path={Uri.EscapeDataString(path)}", $"frameIndex={frameIndex}" };
		if (threadId is { } thread) query.Add($"threadId={thread}");

		return GetAsync<LiveValueExpansion>(
			$"/operator/sessions/{sessionId}/values?{string.Join('&', query)}", Quick, cancellationToken);
	}

	public Task<LiveThreadList> ThreadsAsync(string sessionId, CancellationToken cancellationToken) =>
		GetAsync<LiveThreadList>($"/operator/sessions/{sessionId}/threads", Quick, cancellationToken);

	/// <summary>
	/// Methods of the target's loaded modules matching what has been typed, best first.
	/// <para>
	/// Budgeted like any other quick read rather than given room to be slow. It reads every loaded
	/// module's metadata, which is not free, but it is what a person is waiting on between keystrokes
	/// -- an answer that takes longer than the ten seconds here is one they have already typed past.
	/// </para>
	/// </summary>
	public Task<LiveMethodMatches> MethodsAsync(
		string sessionId,
		string query,
		int limit,
		CancellationToken cancellationToken)
	{
		// Escaped because a qualified query carries dots, and a pasted one can carry anything.
		var escaped = Uri.EscapeDataString(query);

		return GetAsync<LiveMethodMatches>(
			$"/operator/sessions/{sessionId}/methods?query={escaped}&limit={limit}", Quick, cancellationToken);
	}

	/// <summary>A method's source and the positions inside it a breakpoint can be set at.</summary>
	public Task<LiveMethodSource> MethodSourceAsync(
		string sessionId,
		string location,
		CancellationToken cancellationToken)
	{
		// A location carries an exclamation mark, dots, plus signs and the angle brackets of a
		// generated name. None of those may reach the query string unescaped.
		var escaped = Uri.EscapeDataString(location);

		return GetAsync<LiveMethodSource>(
			$"/operator/sessions/{sessionId}/methods/source?location={escaped}", Quick, cancellationToken);
	}

	public Task<LiveXamlTree> XamlTreeAsync(string sessionId, CancellationToken cancellationToken) =>
		GetAsync<LiveXamlTree>($"/operator/sessions/{sessionId}/xaml/tree", Xaml, cancellationToken);

	public Task<LiveXamlProperties> XamlPropertiesAsync(
		string sessionId,
		ulong handle,
		bool includeDefaults,
		CancellationToken cancellationToken) =>
		GetAsync<LiveXamlProperties>(
			$"/operator/sessions/{sessionId}/xaml/elements/{handle}/properties?includeDefaults={Lowered(includeDefaults)}",
			Xaml,
			cancellationToken);

	public Task<LiveXamlSelection> XamlSelectionAsync(string sessionId, CancellationToken cancellationToken) =>
		GetAsync<LiveXamlSelection>($"/operator/sessions/{sessionId}/xaml/selection", Xaml, cancellationToken);

	public Task<LiveXamlSelection> SelectXamlAsync(string sessionId, string element, CancellationToken cancellationToken) =>
		PostAsync<XamlSelectElementRequest, LiveXamlSelection>(
			$"/operator/sessions/{sessionId}/xaml/selection",
			new XamlSelectElementRequest { Element = element },
			Xaml,
			cancellationToken);

	public Task<LiveXamlSelection> DeselectXamlAsync(string sessionId, CancellationToken cancellationToken) =>
		SendAsync<LiveXamlSelection>(
			() => new HttpRequestMessage(HttpMethod.Delete, $"/operator/sessions/{sessionId}/xaml/selection"),
			Xaml,
			cancellationToken);

	public Task<LiveXamlSelection> XamlSelectModeAsync(
		string sessionId,
		XamlSelectModeRequest request,
		CancellationToken cancellationToken) =>
		PostAsync<XamlSelectModeRequest, LiveXamlSelection>(
			$"/operator/sessions/{sessionId}/xaml/select-mode", request, Xaml, cancellationToken);

	public Task<LiveSessionClosed> DetachAsync(string sessionId, CancellationToken cancellationToken) =>
		PostAsync<object?, LiveSessionClosed>($"/operator/sessions/{sessionId}/detach", null, Quick, cancellationToken);

	private static string Lowered(bool value) => value ? "true" : "false";

	private Task<T> GetAsync<T>(string path, TimeSpan budget, CancellationToken cancellationToken) =>
		SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, path), budget, cancellationToken);

	private Task<TResult> PostAsync<TBody, TResult>(
		string path,
		TBody body,
		TimeSpan budget,
		CancellationToken cancellationToken) =>
		SendAsync<TResult>(
			() => new HttpRequestMessage(HttpMethod.Post, path)
			{
				Content = JsonContent.Create(body, options: ContractJson.Options),
			},
			budget,
			cancellationToken);

	/// <summary>
	/// One request, under its own budget, with everything that can go wrong turned into an
	/// <see cref="OperatorException"/> carrying the host's own message where there was one.
	/// <para>
	/// The request is built by a callback rather than passed in, because a budget that expires has
	/// nothing to retry with otherwise: an <c>HttpRequestMessage</c> cannot be sent twice.
	/// </para>
	/// </summary>
	private async Task<T> SendAsync<T>(
		Func<HttpRequestMessage> compose,
		TimeSpan budget,
		CancellationToken cancellationToken)
	{
		using var budgeted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budgeted.CancelAfter(budget);

		try
		{
			using var request = compose();
			using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgeted.Token);

			if (!response.IsSuccessStatusCode) throw await RefusalAsync(response, budgeted.Token);

			var value = await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, budgeted.Token);

			return value ?? throw new OperatorException(
				OperatorFailure.Failed, "The tray answered with no content where a result was expected.", response.StatusCode);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The caller gave up. Not a failure of the tray, and a pane closing mid-request must not
			// put a timeout on screen on its way out.
			throw;
		}
		catch (OperationCanceledException exception)
		{
			throw new OperatorException(
				OperatorFailure.TimedOut,
				$"The tray did not answer within {budget.TotalSeconds:0}s.",
				status: null,
				exception);
		}
		catch (HttpRequestException exception)
		{
			throw new OperatorException(OperatorFailure.Unreachable, InspectorText.NoTray(BaseAddress), status: null, exception);
		}
		catch (JsonException exception)
		{
			throw new OperatorException(
				OperatorFailure.Failed, $"The tray's answer could not be read: {exception.Message}", status: null, exception);
		}
	}

	/// <summary>
	/// Turns a non-success response into the exception for it, preferring the host's own sentence
	/// over anything this end could invent.
	/// </summary>
	private static async Task<OperatorException> RefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var said = await ReasonAsync(response, cancellationToken);

		return response.StatusCode switch
		{
			HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
				new OperatorException(OperatorFailure.Unauthorized, InspectorText.TokenRefused, response.StatusCode),
			HttpStatusCode.NotFound =>
				new OperatorException(OperatorFailure.NotFound, said ?? "That is not there any more.", response.StatusCode),
			HttpStatusCode.BadRequest =>
				new OperatorException(OperatorFailure.Refused, said ?? "The tray refused the request.", response.StatusCode),
			_ => new OperatorException(
				OperatorFailure.Failed,
				said ?? $"The tray answered {(int)response.StatusCode} {response.ReasonPhrase}.",
				response.StatusCode),
		};
	}

	private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			var error = await response.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken);

			return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message;
		}
		catch (Exception)
		{
			// A body that is not an OperatorError is not worth failing over; the status still says
			// something, and inventing a parse error would replace the real one.
			return null;
		}
	}

	/// <summary>
	/// Ends this client. A handler somebody else supplied stays alive, which is what lets one
	/// handler serve several clients in a test.
	/// </summary>
	public void Dispose() => _http.Dispose();
}
