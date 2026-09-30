using ModelContextProtocol.Client;

namespace RoseMcp.Broker;

/// <summary>
/// How the broker opens an MCP session with a child process it started: a worker or a live-app host.
/// <para>
/// The SDK's client first sends <c>server/discover</c> and, if nothing answers within
/// <see cref="McpClientOptions.DiscoverProbeTimeout"/>, falls back to <c>initialize</c> on the same
/// connection. Over stdio that timeout is handed to the send itself, which writes a message's JSON and
/// its terminating newline as separate cancellable writes. A probe that times out between the two
/// leaves the JSON on the pipe without its newline, the fallback <c>initialize</c> is appended to it,
/// and the child reads one line holding two messages. It rejects the line as unparseable, so the
/// <c>initialize</c> it was meant to answer is never answered, and the broker waits out the whole
/// handshake budget for a child that is alive, listening and was never asked anything it could read.
/// A thread pool the size of a busy test run is enough to delay a write past the five-second default.
/// </para>
/// <para>
/// The probe is therefore never timed out on its own. Every child here ships with the broker and
/// answers <c>server/discover</c>, so the fallback it enables can only ever be reached by mistake;
/// the handshake budget still bounds the probe, and a child that does not answer at all fails there.
/// </para>
/// </summary>
public static class ChildHostHandshake
{
	/// <summary>
	/// The client options for a child's session, with <paramref name="budget"/> as the whole handshake
	/// budget, or the SDK's own where none is given.
	/// </summary>
	/// <param name="budget">How long the child has to answer the handshake, probe included.</param>
	public static McpClientOptions Options(TimeSpan? budget = null)
	{
		var options = new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };

		if (budget is { } given) options.InitializationTimeout = given;

		return options;
	}
}
