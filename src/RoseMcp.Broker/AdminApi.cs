using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The unauthenticated <c>/admin</c> routes every http host serves: what the broker has loaded,
/// which debug sessions it supervises, who it is to a relay, and which clients are connected.
/// <para>
/// Mapped from here rather than by each host, because two hosts serving the same broker should not
/// answer different questions, and a route one of them forgot is a deploy that cannot count its
/// relays on one host only. They are gated the way the rest of each host is -- the http server
/// refuses everything without the token where one was configured -- and never by the operator
/// token, because a relay and a deploy are not given it.
/// </para>
/// </summary>
public static class AdminApi
{
	/// <summary>The build every relay is told this broker is.</summary>
	private static readonly BuildIdentity Build = BuildIdentity.Of(typeof(AdminApi).Assembly);

	/// <summary>
	/// The most a relay's introduction may be, in bytes: sixteen roots and a few names come to a few
	/// kilobytes, and a body any larger is refused before it is read.
	/// </summary>
	private const int MaxIntroduction = 64 * 1024;

	/// <summary>
	/// Maps the admin routes. Resolves the relay registry and the activity record now, so their start time is the
	/// host's.
	/// </summary>
	public static WebApplication MapRoseAdminApi(this WebApplication app)
	{
		var relays = app.Services.GetRequiredService<RelayConnections>();

		// Started with the host rather than at the first call, so the record says the broker started when it did.
		app.Services.GetRequiredService<ActivityRecord>();

		// Exactly what the tray window renders, so the two cannot disagree.
		app.MapGet(
			"/admin/workspaces",
			(WorkspaceManager workspaces) => Results.Json(workspaces.Describe(), ContractJson.Options));

		// The live-app sessions alongside the workspaces, from the same shared manager.
		app.MapGet(
			"/admin/sessions",
			(LiveAppSessionManager sessions) => Results.Json(sessions.Describe(), ContractJson.Options));

		app.MapGet(RelayProtocol.HelloRoute, () => Results.Json(relays.Hello(Build), ContractJson.Options));

		app.MapGet(RelayProtocol.WatchRoute, WatchAsync);

		app.MapPost(RelayProtocol.ClientRoute, IntroduceAsync);

		// Who is connected: the relayed sessions, named by what their relays said, and the unnamed clients.
		app.MapGet("/admin/clients", (ConnectedClients clients) => Results.Json(clients.Describe(), ContractJson.Options));

		return app;
	}

	/// <summary>
	/// Holds a relay's request open until this broker stops or the relay goes, counting the relay as
	/// connected meanwhile.
	/// <para>
	/// The hello goes out first and is flushed, so the relay knows at once that the watch is
	/// established and which broker holds it. After that nothing is written: what the relay waits for
	/// is the response ending, and a killed broker ends it by closing the socket, which is the case a
	/// deploy produces. Stopping ends it too, deliberately, so a graceful stop is not held up for the
	/// whole shutdown timeout by requests that would otherwise never finish.
	/// </para>
	/// </summary>
	private static async Task WatchAsync(HttpContext context, RelayConnections relays, IHostApplicationLifetime lifetime)
	{
		var relayId = context.Request.Headers[RelayProtocol.RelayHeader].ToString();
		if (!RelayProtocol.IsRelayId(relayId))
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync(
				$"A relay watches with its id in the {RelayProtocol.RelayHeader} header: thirty-two lowercase hex digits.",
				context.RequestAborted);

			return;
		}

		var processId = RelayConnections.ParseProcessId(context.Request.Headers[RelayProtocol.ProcessHeader].ToString());
		using var watching = relays.Watch(relayId, processId);
		using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);

		try
		{
			await context.Response.WriteAsJsonAsync(relays.Hello(Build), ContractJson.Options, "application/x-ndjson", ended.Token);
			await context.Response.WriteAsync("\n", ended.Token);
			await context.Response.Body.FlushAsync(ended.Token);

			await Task.Delay(Timeout.InfiniteTimeSpan, ended.Token);
		}
		catch (OperationCanceledException) when (ended.IsCancellationRequested)
		{
			// The relay went, or this broker is stopping: either way the watch is over.
		}
	}

	/// <summary>
	/// Takes what a relay says of the session it serves, answering 204 once it is kept.
	/// <para>
	/// Every field is a claim from some process on this machine, so the body is held to
	/// <see cref="MaxIntroduction"/> bytes before it is read, and <see cref="ConnectedClients.Sanitised"/>
	/// cleans each field before it is kept. A malformed body, relay id or session is a 400 saying which; a
	/// session another relay already named is a 409, so one relay cannot write over another's facts.
	/// </para>
	/// </summary>
	private static async Task IntroduceAsync(HttpContext context, ConnectedClients clients)
	{
		var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
		if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxIntroduction;

		if (context.Request.ContentLength > MaxIntroduction)
		{
			await RefuseAsync(context, StatusCodes.Status413PayloadTooLarge, $"A relay's introduction is at most {MaxIntroduction} bytes.");
			return;
		}

		RelayClient? client;

		try
		{
			client = await context.Request.ReadFromJsonAsync<RelayClient>(ContractJson.Options, context.RequestAborted);
		}
		catch (Exception exception) when (exception is JsonException or BadHttpRequestException or InvalidOperationException)
		{
			await RefuseAsync(context, StatusCodes.Status400BadRequest, $"A relay introduces its session with a JSON body: {exception.Message}");
			return;
		}

		if (client is null)
		{
			await RefuseAsync(context, StatusCodes.Status400BadRequest, "A relay introduces its session with a JSON body, and this one was empty.");
			return;
		}

		try
		{
			clients.Introduce(context.Request.Headers[RelayProtocol.RelayHeader].ToString(), client);
			context.Response.StatusCode = StatusCodes.Status204NoContent;
		}
		catch (ArgumentException exception)
		{
			await RefuseAsync(context, StatusCodes.Status400BadRequest, exception.Message);
		}
		catch (InvalidOperationException exception)
		{
			await RefuseAsync(context, StatusCodes.Status409Conflict, exception.Message);
		}
	}

	private static async Task RefuseAsync(HttpContext context, int status, string reason)
	{
		context.Response.StatusCode = status;
		await context.Response.WriteAsync(reason, context.RequestAborted);
	}
}
