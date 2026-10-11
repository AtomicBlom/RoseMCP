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

	/// <summary>The token each request presents, read as it is sent; see <see cref="Reauthorise"/>.</summary>
	private string? _token;

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

		BaseAddress = baseAddress;
		_ = Reauthorise(token);
	}

	public Uri BaseAddress { get; }

	/// <summary>Whether this client has a token at all, which decides which empty state to show.</summary>
	public bool HasToken => !string.IsNullOrWhiteSpace(Volatile.Read(ref _token));

	/// <summary>
	/// The token every request from here on presents, in place of the one this client was made with.
	/// <para>
	/// A broker mints its token per run, so a window left open across a restart is refused until somebody opens
	/// RoseMCP from the tray again -- and that launch is handed to the app already running, carrying the new run's
	/// token. Taking it here rather than making a new client is what lets every window and every poll in the app
	/// carry on with the requests they already hold; each request reads the token as it is sent, so one in flight
	/// keeps the token it went with and the next takes the new one.
	/// </para>
	/// </summary>
	/// <returns>Whether the token is a different one from the token this client held.</returns>
	public bool Reauthorise(string? token)
	{
		var next = string.IsNullOrWhiteSpace(token) ? null : token;
		var previous = Interlocked.Exchange(ref _token, next);

		return !string.Equals(previous, next, StringComparison.Ordinal);
	}

	/// <summary>
	/// What a request that waits on a workspace gets: reading a status, which answers once the solution has loaded,
	/// and reloading or closing one, which stops a worker and starts another. Minutes, because a large solution's
	/// load is.
	/// </summary>
	public static readonly TimeSpan WorkspaceWait = TimeSpan.FromMinutes(3);

	/// <summary>
	/// A workspace's full status, its projects included: what a degraded row's detail reads when somebody opens it.
	/// Nothing is started for it, and reading it does not count as using the workspace.
	/// </summary>
	public Task<WorkspaceStatusReport> WorkspaceStatusAsync(string workspace, CancellationToken cancellationToken) =>
		GetAsync<WorkspaceStatusReport>(
			$"/operator/workspaces/status?workspace={Uri.EscapeDataString(workspace)}&projects=true", WorkspaceWait, cancellationToken);

	/// <summary>Reloads a workspace: a fresh worker, under the build it was last asked for. Answers with the new row, before its load.</summary>
	public Task<OperatorWorkspace> ReloadWorkspaceAsync(string workspace, CancellationToken cancellationToken) =>
		PostAsync<OperatorWorkspaceRequest, OperatorWorkspace>(
			"/operator/workspaces/reload", new OperatorWorkspaceRequest { Workspace = workspace }, WorkspaceWait, cancellationToken);

	/// <summary>Closes a workspace, stopping its worker for every agent that shares it.</summary>
	public Task<WorkspaceClosed> CloseWorkspaceAsync(string workspace, CancellationToken cancellationToken) =>
		PostAsync<OperatorWorkspaceRequest, WorkspaceClosed>(
			"/operator/workspaces/close", new OperatorWorkspaceRequest { Workspace = workspace }, WorkspaceWait, cancellationToken);

	/// <summary>Every reason the person has accepted for one solution, with what agents read about each.</summary>
	public Task<AcceptedReasons> AcceptedAsync(string workspace, CancellationToken cancellationToken) =>
		GetAsync<AcceptedReasons>($"/operator/health/accepted?workspace={Uri.EscapeDataString(workspace)}", Quick, cancellationToken);

	/// <summary>Accepts a reason for the projects or subjects it covers, choosing what agents read about it.</summary>
	public Task<AcceptedReasons> AcceptAsync(AcceptReasonRequest request, CancellationToken cancellationToken) =>
		PostAsync<AcceptReasonRequest, AcceptedReasons>("/operator/health/accept", request, Quick, cancellationToken);

	/// <summary>Stops accepting a reason, so it draws attention and agents hear Rose's line again.</summary>
	public Task<AcceptedReasons> StopAcceptingAsync(StopAcceptingRequest request, CancellationToken cancellationToken) =>
		PostAsync<StopAcceptingRequest, AcceptedReasons>("/operator/health/stop-accepting", request, Quick, cancellationToken);

	/// <summary>Every reason an agent has proposed the person accept, waiting to be decided.</summary>
	public Task<IReadOnlyList<ReasonProposal>> ProposalsAsync(CancellationToken cancellationToken) =>
		GetAsync<IReadOnlyList<ReasonProposal>>("/operator/health/proposals", Quick, cancellationToken);

	/// <summary>Accepts what a proposal proposed, with what agents read changed where <paramref name="request"/> says.</summary>
	public Task<AcceptedReasons> ConfirmProposalAsync(string proposalId, ConfirmProposalRequest? request, CancellationToken cancellationToken) =>
		PostAsync<ConfirmProposalRequest?, AcceptedReasons>(
			$"/operator/health/proposals/{Uri.EscapeDataString(proposalId)}/confirm", request, Quick, cancellationToken);

	/// <summary>Drops a proposal without accepting anything.</summary>
	public Task<ReasonProposal> RejectProposalAsync(string proposalId, CancellationToken cancellationToken) =>
		PostAsync<object?, ReasonProposal>(
			$"/operator/health/proposals/{Uri.EscapeDataString(proposalId)}/reject", null, Quick, cancellationToken);

	/// <summary>
	/// Sends a note to an agent session, to ride on the answer to its next Rose call of any tool. Answers with the
	/// note as the broker holds it, waiting for that call.
	/// </summary>
	public Task<AgentNote> SendNoteAsync(NoteRequest request, CancellationToken cancellationToken) =>
		PostAsync<NoteRequest, AgentNote>("/operator/notes", request, Quick, cancellationToken);

	/// <summary>
	/// Every note the broker holds, with where each one's delivery has got to and what the agent answered. A long poll
	/// where <paramref name="after"/> and <paramref name="waitSeconds"/> say, answered when a note is sent, delivered or
	/// answered, or its session goes.
	/// </summary>
	public Task<NoteList> NotesAsync(string? after, int waitSeconds, CancellationToken cancellationToken) =>
		GetAsync<NoteList>(Watched("/operator/notes", after, waitSeconds), PollBudget(waitSeconds), cancellationToken);

	/// <summary>
	/// Which build the broker is, and how far its checkout has moved on. Given the read budget: the
	/// broker waits a couple of seconds at most for its checkout's commits to be counted and otherwise
	/// answers that they are still being counted, so the build always arrives inside it.
	/// </summary>
	public Task<OperatorHello> HelloAsync(CancellationToken cancellationToken) =>
		GetAsync<OperatorHello>("/operator/hello", Quick, cancellationToken);

	/// <summary>Every debug session on the machine, whoever started it.</summary>
	public async Task<IReadOnlyList<LiveAppSessionSummary>> SessionsAsync(CancellationToken cancellationToken) =>
		(await SessionListAsync(after: null, waitSeconds: 0, cancellationToken)).Sessions;

	/// <summary>
	/// Every debug session on the machine with the view's version. Given <paramref name="after"/>, the version of the
	/// last answer, and a <paramref name="waitSeconds"/> above zero, a long poll answered when a session starts, ends
	/// or changes state -- so the budget is the wait plus a margin.
	/// </summary>
	public Task<OperatorSessionList> SessionListAsync(string? after, int waitSeconds, CancellationToken cancellationToken) =>
		GetAsync<OperatorSessionList>(Watched("/operator/sessions", after, waitSeconds), PollBudget(waitSeconds), cancellationToken);

	/// <summary>
	/// The broker's clients: each agent session behind a relay, and the clients without one. A long poll where
	/// <paramref name="after"/> and <paramref name="waitSeconds"/> say, answered when a client connects, calls or goes.
	/// </summary>
	public Task<ClientList> ClientsAsync(string? after, int waitSeconds, CancellationToken cancellationToken) =>
		GetAsync<ClientList>(Watched("/operator/clients", after, waitSeconds), PollBudget(waitSeconds), cancellationToken);

	/// <summary>
	/// Every workspace the broker holds a worker for, with its health, folder, configuration, platform, memory, idle
	/// time and close time. A long poll where <paramref name="after"/> and <paramref name="waitSeconds"/> say, answered
	/// when a row changes in anything but its memory and its clock.
	/// </summary>
	public Task<OperatorWorkspaceList> WorkspacesAsync(string? after, int waitSeconds, CancellationToken cancellationToken) =>
		GetAsync<OperatorWorkspaceList>(Watched("/operator/workspaces", after, waitSeconds), PollBudget(waitSeconds), cancellationToken);

	/// <summary>
	/// A page of the activity record: the newest entries <paramref name="filter"/> matches, or those older than
	/// <paramref name="before"/>, the <c>next</c> of the page before. A long poll where <paramref name="after"/> and
	/// <paramref name="waitSeconds"/> say, answered when a call the filter matches starts or ends.
	/// </summary>
	public Task<ActivityPage> ActivityAsync(
		ActivityFilter filter,
		string? before,
		string? after,
		int waitSeconds,
		CancellationToken cancellationToken)
	{
		var query = new List<string>(filter.Arguments());
		if (!string.IsNullOrEmpty(before)) query.Add($"before={Uri.EscapeDataString(before)}");

		var path = query.Count == 0 ? "/operator/activity" : $"/operator/activity?{string.Join('&', query)}";

		return GetAsync<ActivityPage>(Watched(path, after, waitSeconds), PollBudget(waitSeconds), cancellationToken);
	}

	/// <summary>One entry of the activity record, with its arguments, response and diffs.</summary>
	public Task<ActivityEntry> ActivityEntryAsync(string id, CancellationToken cancellationToken) =>
		GetAsync<ActivityEntry>($"/operator/activity/{Uri.EscapeDataString(id)}", Quick, cancellationToken);

	/// <summary>What the broker, each worker, each live-app host and each debugged app hold, and the machine's memory, sampled now.</summary>
	public Task<MemoryReport> MemoryAsync(CancellationToken cancellationToken) =>
		GetAsync<MemoryReport>("/operator/memory", Quick, cancellationToken);

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

	/// <summary>
	/// <paramref name="path"/> with the long poll's arguments added, where there is a version to wait past and a
	/// wait to do it in; unchanged otherwise, which reads the view at once.
	/// </summary>
	private static string Watched(string path, string? after, int waitSeconds)
	{
		if (string.IsNullOrEmpty(after) || waitSeconds <= 0) return path;

		var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
		return $"{path}{separator}after={Uri.EscapeDataString(after)}&waitSeconds={waitSeconds}";
	}

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
			if (Volatile.Read(ref _token) is { } token) request.Headers.Authorization = new("Bearer", token);

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
