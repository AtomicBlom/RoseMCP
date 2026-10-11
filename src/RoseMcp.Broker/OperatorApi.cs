using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RoseMcp.Contracts;
using RoseMcp.Logging;

namespace RoseMcp.Broker;

/// <summary>
/// The http surface a person's tools read a broker through, behind a bearer token: who is connected, which
/// workspaces are loaded, what every client did, every debug session on the machine and everything one can be
/// asked, and what all of it holds in memory. A window reads nothing of the broker's any other way, so it can run
/// in a process of its own and a test can drive it.
/// <para>
/// A view that changes is kept current by a long poll rather than by asking every second: its answer carries a
/// version, and the same read with that version as <c>after</c> and a <c>waitSeconds</c> is answered when the view
/// holds something else. See <see cref="LongPoll"/>.
/// </para>
/// <para>
/// It exists because the MCP surface cannot serve it. Every agent-facing debug tool resolves a
/// session through <see cref="LiveAppSessionManager.Find"/>, which serves a client only the sessions
/// it started -- and inside an http endpoint <see cref="CallSession.Id"/> is null, because the filter
/// that sets it runs for tool calls only. So an MCP client is exactly the wrong shape for a window
/// that has to show sessions somebody else started, which is all of them.
/// </para>
/// <para>
/// Mapped from this library rather than from each host, because there are two hosts and an operator
/// looking at one should not find a different surface than at the other. The token is the host's to
/// mint: it is the thing that knows whether the person asking is at the keyboard.
/// </para>
/// </summary>
public static class OperatorApi
{
	/// <summary>Everything here hangs off one prefix, so one middleware branch can gate all of it.</summary>
	public const string Prefix = "/operator";

	/// <summary>
	/// Maps the operator API and gates it on <paramref name="token"/>.
	/// <para>
	/// The token check is middleware on a path branch rather than an endpoint filter, and that is
	/// deliberate: middleware runs before model binding, so a request with no token is refused
	/// before a malformed route value can turn it into a 400 that says nothing about the token.
	/// </para>
	/// </summary>
	public static RouteGroupBuilder MapRoseOperatorApi(this WebApplication app, OperatorToken token)
	{
		app.UseWhen(
			context => context.Request.Path.StartsWithSegments(Prefix),
			branch => branch.Use(async (context, next) =>
			{
				if (!token.Matches(context.Request.Headers.Authorization.ToString()))
				{
					context.Response.StatusCode = StatusCodes.Status401Unauthorized;
					context.Response.Headers.WWWAuthenticate = "Bearer";

					await context.Response.WriteAsJsonAsync(
						new OperatorError
						{
							Status = StatusCodes.Status401Unauthorized,
							Message = "This endpoint needs the operator token for this run of the broker. The tray "
								+ "mints one when it starts and passes it to the window it launches; use Open "
								+ "RoseMCP on the tray's icon.",
						},
						ContractJson.Options);

					return;
				}

				await next(context);
			}));

		// An operator request is a call entering Rose as surely as a tool call is: an inspector's step or
		// pick is forwarded to a live-app host like any tool, so it gets an id here, first, for the lines
		// it writes on both sides of that hop.
		var operators = app.MapGroup(Prefix)
			.AddEndpointFilter(async (context, next) =>
			{
				using var correlation = CallCorrelation.Begin(null);

				return await next(context);
			})
			.AddEndpointFilter(new Failures());

		MapHello(operators);
		MapSessions(operators);
		MapClients(operators);
		MapWorkspaces(operators);
		MapActivity(operators);
		MapMemory(operators);
		MapDebugging(operators);
		MapXaml(operators);
		MapHealth(operators);

		// This host now has somewhere a person decides what agents propose, so proposing is worth allowing.
		app.Services.GetRequiredService<WorkspaceHealth>().OfferToThePerson();

		return operators;
	}

	/// <summary>
	/// Which build the broker is, and how far its checkout has moved on: the first thing a window
	/// asks, so it can say which build it talks to and whether that is its own.
	/// </summary>
	private static void MapHello(RouteGroupBuilder operators) =>
		operators.MapGet(
			"/hello",
			async (CheckoutDistanceReader distances, HttpContext context) => Json(new OperatorHello
			{
				Build = distances.Build,
				Checkout = await distances.ReadAsync(context.RequestAborted),
			}));

	/// <summary>
	/// The person's side of a workspace's health: accepting a reason and choosing what agents read about it,
	/// stopping, and deciding what agents proposed.
	/// <para>
	/// Here and not on the MCP surface, because an acceptance outlives the session that makes it and changes
	/// what every agent reads: an agent able to make one, or to confirm its own proposal, could silence a
	/// reason for every other agent on the machine. The token is what says the person is the one asking.
	/// </para>
	/// </summary>
	private static void MapHealth(RouteGroupBuilder operators)
	{
		var routes = operators.MapGroup("/health");

		routes.MapGet("/accepted", (string workspace, WorkspaceHealth health) => Json(health.Accepted(workspace)));

		routes.MapPost(
			"/accept",
			(AcceptReasonRequest body, WorkspaceHealth health) =>
				Json(health.Accept(body.Workspace, body.Kind, body.Projects, body.AgentsRead, body.Note, body.Subjects)));

		routes.MapPost(
			"/stop-accepting",
			(StopAcceptingRequest body, WorkspaceHealth health) =>
				Json(health.StopAccepting(body.Workspace, body.Kind, body.Projects, body.Subjects)));

		routes.MapGet("/proposals", (WorkspaceHealth health) => Json(health.Proposals()));

		// The body is optional: confirming as proposed is the common case and needs nothing said.
		routes.MapPost(
			"/proposals/{proposalId}/confirm",
			(string proposalId, WorkspaceHealth health, ConfirmProposalRequest? body) =>
				Json(health.Confirm(proposalId, body?.AgentsRead, body?.Note)));

		routes.MapPost(
			"/proposals/{proposalId}/reject",
			(string proposalId, WorkspaceHealth health) => Json(health.Reject(proposalId)));
	}

	/// <summary>
	/// Which sessions there are, with the version a window passes back as <c>after</c>, with <c>waitSeconds</c>, to be
	/// answered when one starts, ends or changes state; one session; and ending one.
	/// </summary>
	private static void MapSessions(RouteGroupBuilder operators)
	{
		operators.MapGet(
			"/sessions",
			async (OperatorViews views, LongPoll poll, HttpContext context, string? after = null, int waitSeconds = 0) =>
				Json(await poll.WaitAsync(
					views.Sessions, list => list.Version, after, waitSeconds, views.ActivityChanged, context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}",
			(string sessionId, LiveAppSessionManager sessions) => Json(Require(sessions, sessionId).Describe()));

		operators.MapPost(
			"/sessions/{sessionId}/detach",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
			{
				// Held before the close, because DetachFailure is on the session and the close forgets it.
				var session = Require(sessions, sessionId);
				var closed = await sessions.CloseForOperatorAsync(sessionId, context.RequestAborted);

				return Json(new LiveSessionClosed { Closed = closed, DetachFailure = session.DetachFailure });
			});
	}

	/// <summary>
	/// Which clients are connected: each agent session behind a relay, named by what its relay said of it, and
	/// the clients that reached the broker without one. The same table <c>GET /admin/clients</c> reads, with the
	/// version a window passes back as <c>after</c>, with <c>waitSeconds</c>, to be answered when the table changes.
	/// </summary>
	private static void MapClients(RouteGroupBuilder operators) =>
		operators.MapGet(
			"/clients",
			async (OperatorViews views, LongPoll poll, HttpContext context, string? after = null, int waitSeconds = 0) =>
				Json(await poll.WaitAsync(
					views.Clients, list => list.Version, after, waitSeconds, views.ClientsChanged, context.RequestAborted)));

	/// <summary>
	/// The activity record: the newest entries for a client, a workspace, a debug session or a tool in a window of
	/// time, only the failures or only those naming some text where asked, a page at a time, without their bodies
	/// unless asked; and one entry by its id with them. Under the operator token rather than on the unauthenticated
	/// admin routes, since an entry carries what an agent sent and was answered, source included. Reading it is not
	/// recorded: these are not MCP calls.
	/// <para>
	/// A page's <c>next</c> passed back as <c>before</c> reads the page after it. Its <c>version</c> passed back as
	/// <c>after</c>, with <c>waitSeconds</c> and the same filters, answers when a call matching them starts or ends.
	/// The wait compares pages read without their bodies, which leave the version unchanged, so a window holding a
	/// page of bodies open does not read fifty bodies back from disk every second.
	/// </para>
	/// </summary>
	private static void MapActivity(RouteGroupBuilder operators)
	{
		operators.MapGet(
			"/activity",
			async (
				OperatorViews views,
				LongPoll poll,
				HttpContext context,
				string? client,
				string? workspace,
				string? session,
				string? tool,
				DateTimeOffset? since,
				DateTimeOffset? until,
				int? limit,
				bool? bodies,
				bool? failures,
				string? text,
				string? before,
				string? after,
				int? waitSeconds) =>
			{
				var query = new ActivityQuery
				{
					Client = client,
					Workspace = workspace,
					DebugSession = session,
					Tool = tool,
					Since = since,
					Until = until,
					Limit = limit ?? ActivityQuery.DefaultLimit,
					Bodies = bodies ?? false,
					Failures = failures ?? false,
					Text = text,
					Before = before,
				};

				var waited = await poll.WaitAsync(
					() => views.Activity(OperatorViews.WithoutBodies(query)),
					page => page.Version,
					after,
					waitSeconds ?? 0,
					views.ActivityChanged,
					context.RequestAborted);

				return Json(query.Bodies ? views.Activity(query) : waited);
			});

		operators.MapGet(
			"/activity/{id}",
			(ActivityRecord record, string id) => Json(record.Find(id) ?? throw new KeyNotFoundException(
				$"The activity record has no entry {id}. It keeps an entry for {record.Retention.TotalDays:0.#} day(s).")));
	}

	/// <summary>
	/// Which workspaces are loaded: each worker's row as the tray reads it, with its folder, what it was loaded under,
	/// its idle time and when it will close, and the version a window passes back as <c>after</c>, with
	/// <c>waitSeconds</c>, to be answered when a row changes. Read from the broker alone, so it never waits on a
	/// load and never counts as using a workspace. The wait reads the rows without their memory, which the version
	/// leaves out, and the answer samples it once.
	/// </summary>
	private static void MapWorkspaces(RouteGroupBuilder operators) =>
		operators.MapGet(
			"/workspaces",
			async (OperatorViews views, LongPoll poll, HttpContext context, string? after = null, int waitSeconds = 0) =>
			{
				// Calls are what change a workspace -- its running work, its last use, the status a call brings back --
				// so the activity record's pulse is the one to wake on.
				await poll.WaitAsync(
					() => views.Workspaces(sampleMemory: false),
					list => list.Version,
					after,
					waitSeconds,
					views.ActivityChanged,
					context.RequestAborted);

				return Json(views.Workspaces());
			});

	/// <summary>
	/// What Rose holds in memory -- the broker, each worker and each live-app host -- what each debugged app holds
	/// apart from it, and the machine's own. Sampled when asked and never waited on: every figure differs from one
	/// read to the next, so a window reads it on a timer.
	/// </summary>
	private static void MapMemory(RouteGroupBuilder operators) =>
		operators.MapGet("/memory", (OperatorViews views) => Json(views.Memory()));

	/// <summary>Watching a target and steering it.</summary>
	private static void MapDebugging(RouteGroupBuilder operators)
	{
		// The wait is passed through rather than capped here: the host bounds it, and a reader that
		// asked to wait a minute for the next exception should get a minute.
		operators.MapGet(
			"/sessions/{sessionId}/events",
			async (
				string sessionId,
				LiveAppSessionManager sessions,
				HttpContext context,
				long after = 0,
				string? kinds = null,
				int limit = 500,
				int waitSeconds = 0) =>
				Json(await Require(sessions, sessionId).ReadEventsAsync(
					after,
					Split(kinds),
					limit,
					waitSeconds,
					context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/breakpoints",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ListBreakpointsAsync(context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/breakpoints",
			async (string sessionId, SetBreakpointRequest body, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).SetBreakpointAsync(
					body.Location, body.AutoContinueSeconds, body.Condition, context.RequestAborted)));

		operators.MapDelete(
			"/sessions/{sessionId}/breakpoints/{breakpointId}",
			async (string sessionId, string breakpointId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).RemoveBreakpointAsync(breakpointId, context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/tracepoints",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ListTracepointsAsync(context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/tracepoints",
			async (string sessionId, AddTracepointRequest body, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).AddTracepointAsync(
					body.Location, body.LogMessage, body.LogEveryNthHit, body.Condition, context.RequestAborted)));

		operators.MapDelete(
			"/sessions/{sessionId}/tracepoints/{tracepointId}",
			async (string sessionId, string tracepointId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).RemoveTracepointAsync(tracepointId, context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/continue",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ResumeAsync(context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/step",
			async (string sessionId, StepRequest body, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).StepDetailedAsync(body.Mode, context.RequestAborted)));

		// No body: a pause takes the target where it is, and the only thing to say about it is how
		// long an unattended one should last, which the default answers.
		operators.MapPost(
			"/sessions/{sessionId}/break",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context, int? autoContinueSeconds = null) =>
				Json(await Require(sessions, sessionId).BreakAsync(autoContinueSeconds, context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/hold",
			async (string sessionId, HoldRequest body, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).HoldAsync(body.Seconds, body.Release, context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/frames",
			async (
				string sessionId,
				LiveAppSessionManager sessions,
				HttpContext context,
				int? threadId = null,
				int offset = 0,
				int? limit = null) =>
				Json(await Require(sessions, sessionId).ReadFramesAsync(threadId, offset, limit, context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/frames/{frameIndex}/variables",
			async (
				string sessionId,
				int frameIndex,
				LiveAppSessionManager sessions,
				HttpContext context,
				int? threadId = null) =>
				Json(await Require(sessions, sessionId).ReadFrameVariablesAsync(
					frameIndex, threadId, context.RequestAborted)));

		// The path is a query argument rather than a route segment because it carries dots and
		// brackets, and a segment holding those is one nobody can read or escape reliably.
		operators.MapGet(
			"/sessions/{sessionId}/values",
			async (
				string sessionId,
				string path,
				LiveAppSessionManager sessions,
				HttpContext context,
				int frameIndex = 0,
				int? threadId = null) =>
				Json(await Require(sessions, sessionId).ExpandValueAsync(
					path, frameIndex, threadId, context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/threads",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ReadThreadsAsync(context.RequestAborted)));

		// Both of these read module files rather than the target, so they answer while it is running
		// and while it is wedged -- which is when somebody most wants to set a breakpoint.
		operators.MapGet(
			"/sessions/{sessionId}/methods",
			async (
				string sessionId,
				string query,
				LiveAppSessionManager sessions,
				HttpContext context,
				int limit = 30) =>
				Json(await Require(sessions, sessionId).SearchMethodsAsync(query, limit, context.RequestAborted)));

		// The location is a query argument for the same reason a value path is: it carries dots,
		// brackets, an exclamation mark and sometimes angle brackets, and no route segment survives
		// all of those legibly.
		operators.MapGet(
			"/sessions/{sessionId}/methods/source",
			async (string sessionId, string location, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ReadMethodSourceAsync(location, context.RequestAborted)));
	}

	/// <summary>Reading and pointing at a live visual tree.</summary>
	private static void MapXaml(RouteGroupBuilder operators)
	{
		// The whole tree, unpaged. Paging exists for the agent surface, where a tree has to fit in a
		// model's context; a window holding the tree in memory has no such limit and would only have
		// to stitch the pages back together.
		operators.MapGet(
			"/sessions/{sessionId}/xaml/tree",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ReadXamlTreeAsync(context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/xaml/elements/{handle}/properties",
			async (
				string sessionId,
				ulong handle,
				LiveAppSessionManager sessions,
				HttpContext context,
				bool includeDefaults = false) =>
				Json(await Require(sessions, sessionId).ReadXamlPropertiesAsync(
					handle, includeDefaults, context.RequestAborted)));

		operators.MapGet(
			"/sessions/{sessionId}/xaml/selection",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ReadXamlSelectionAsync(context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/xaml/selection",
			async (string sessionId, XamlSelectElementRequest body, LiveAppSessionManager sessions, HttpContext context) =>
			{
				var session = Require(sessions, sessionId);
				var handle = await session.ResolveElementAsync(body.Element, context.RequestAborted);

				return Json(await session.SelectXamlElementAsync(handle, context.RequestAborted));
			});

		operators.MapDelete(
			"/sessions/{sessionId}/xaml/selection",
			async (string sessionId, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).ClearXamlSelectionAsync(context.RequestAborted)));

		operators.MapPost(
			"/sessions/{sessionId}/xaml/select-mode",
			async (string sessionId, XamlSelectModeRequest body, LiveAppSessionManager sessions, HttpContext context) =>
				Json(await Require(sessions, sessionId).EnterXamlSelectModeAsync(
					body.IncludeAllElements, body.JustMyXaml, body.Arm, context.RequestAborted)));
	}

	/// <summary>
	/// The session that id names, whoever started it.
	/// <para>
	/// Deliberately <see cref="LiveAppSessionManager.ForOperator"/> and never
	/// <see cref="LiveAppSessionManager.Find"/>: see the note on this type for why the latter refuses
	/// every session from here. A session dropped because its host died is refused saying so, which is
	/// the one way a session goes that its reader did not cause.
	/// </para>
	/// </summary>
	/// <exception cref="KeyNotFoundException">There is no session with that id.</exception>
	private static LiveAppSession Require(LiveAppSessionManager sessions, string sessionId)
	{
		if (sessions.ForOperator(sessionId) is { } session) return session;

		if (sessions.DroppedForOperator(sessionId) is { } dropped)
		{
			throw new KeyNotFoundException(
				dropped.Explain(sessions.UtcNow, "Start or attach a new session to debug the target again."));
		}

		throw new KeyNotFoundException(
			$"No debug session '{sessionId}' is open. GET /operator/sessions lists the ones there are. "
				+ "A session ends when its client disconnects, when something detaches it, or when its "
				+ "live-app host stops answering.");
	}

	/// <summary>
	/// Event kinds as a comma-separated query value, which is what a URL can carry. Empty means every
	/// kind rather than none: a filter nobody set should not hide everything.
	/// </summary>
	private static string[]? Split(string? kinds) =>
		string.IsNullOrWhiteSpace(kinds) ? null : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// Every answer goes out through the Contracts serialiser, so the JSON matches what the admin
	/// endpoints and the inspector's own deserialiser expect -- string enums included, since a reader
	/// shown a number learns nothing.
	/// </summary>
	private static IResult Json<T>(T value) => Results.Json(value, ContractJson.Options);

	/// <summary>
	/// Turns what the broker throws into a status and a message a caller can act on.
	/// <para>
	/// The real message travels, which is the same rule the MCP boundary follows: the alternative is
	/// a caller told only that something failed, and the guesses that follow are expensive. The three
	/// kinds are separated because a client does different things with them -- fix the request, drop
	/// the session from its list, or report and retry.
	/// </para>
	/// </summary>
	private sealed class Failures : IEndpointFilter
	{
		public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
		{
			try
			{
				return await next(context);
			}
			catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
			{
				// The caller went away. There is nobody left to answer, and the host has already been
				// told to stop: a long poll that is abandoned cancels the read at the far end.
				return Results.Empty;
			}
			catch (KeyNotFoundException exception)
			{
				return Fail(exception, StatusCodes.Status404NotFound);
			}
			catch (ArgumentException exception)
			{
				return Fail(exception, StatusCodes.Status400BadRequest);
			}
			catch (Exception exception)
			{
				return Fail(exception, StatusCodes.Status500InternalServerError);
			}
		}

		private static IResult Fail(Exception exception, int status) =>
			Results.Json(
				new OperatorError { Message = exception.Message, Status = status },
				ContractJson.Options,
				statusCode: status);
	}
}
