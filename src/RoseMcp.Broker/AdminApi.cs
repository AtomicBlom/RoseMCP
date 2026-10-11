using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The unauthenticated <c>/admin</c> routes every http host serves: what the broker has loaded,
/// which debug sessions it supervises, and who it is to a relay.
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

	/// <summary>Maps the admin routes. Resolves the relay registry now, so its start time is the host's.</summary>
	public static WebApplication MapRoseAdminApi(this WebApplication app)
	{
		var relays = app.Services.GetRequiredService<RelayConnections>();

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
}
