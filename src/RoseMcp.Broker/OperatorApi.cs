using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The http surface a person's tools read a broker through: every debug session on the machine, and
/// everything one can be asked, behind a bearer token.
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
								+ "mints one when it starts and passes it to the inspector it launches; use Open "
								+ "inspector, or Copy inspector command, from the tray.",
						},
						ContractJson.Options);

					return;
				}

				await next(context);
			}));

		var operators = app.MapGroup(Prefix).AddEndpointFilter(new Failures());

		MapSessions(operators);
		MapDebugging(operators);
		MapXaml(operators);

		return operators;
	}

	/// <summary>Which sessions there are, and ending one.</summary>
	private static void MapSessions(RouteGroupBuilder operators)
	{
		operators.MapGet("/sessions", (LiveAppSessionManager sessions) => Json(sessions.Describe()));

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
	/// every session from here.
	/// </para>
	/// </summary>
	/// <exception cref="KeyNotFoundException">There is no session with that id.</exception>
	private static LiveAppSession Require(LiveAppSessionManager sessions, string sessionId) =>
		sessions.ForOperator(sessionId)
			?? throw new KeyNotFoundException(
				$"No debug session '{sessionId}' is open. GET /operator/sessions lists the ones there are. "
					+ "A session ends when its client disconnects or something detaches it.");

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
