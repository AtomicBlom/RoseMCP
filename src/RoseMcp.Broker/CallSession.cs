using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RoseMcp.Broker;

/// <summary>
/// Which MCP session made this call, for the length of that call.
/// <para>
/// A live-app session is a debugger attached to somebody's running program, and the broker is a
/// singleton every connection shares. Without an owner recorded against each one, any client of an
/// http broker can read another client's captured exceptions and log output, set breakpoints in its
/// target, evaluate expressions inside it, and detach it -- by guessing an eight-character id, or by
/// reading GET /admin/sessions, which lists them.
/// </para>
/// <para>
/// Ambient rather than a tool parameter, for the reason <see cref="CallOrigin"/> is: threading it
/// through every tool method means the tool added next is the one that forgets, and a debugging
/// surface of twenty-two tools is exactly where that happens. Null is the honest value for a stdio
/// broker, which has one session for the life of the process, so every call in it owns everything
/// it started and nothing else can reach the port at all.
/// </para>
/// <para>
/// Two values, not one. <see cref="Id"/> is the transport's session, which is what owns a debug session.
/// <see cref="Client"/> is that, or else the name a relay gives the session it serves, which is what a
/// workspace's acknowledged reasons belong to. They are kept apart because a debugger left attached by a
/// conversation that has ended must stay reachable as it always was, and live-app sessions never expire;
/// scoping debug ownership to relayed sessions is its own change, with its own answer for that.
/// </para>
/// </summary>
public static class CallSession
{
	/// <summary>
	/// Where in a tool call's <c>_meta</c> a stdio relay names the session it serves, beside the directory it
	/// started in.
	/// </summary>
	public const string MetaKey = "rosemcp/session";

	/// <summary>
	/// Where in a call's <c>_meta</c> a relay marks a read it makes for itself under its session's name -- which
	/// debug sessions are open, after a call that may have changed them -- so the broker does not count it as one
	/// of the session's calls.
	/// </summary>
	public const string RelayReadKey = "rosemcp/relayRead";

	/// <summary>Whether <paramref name="meta"/> marks the call as a relay's own read (<see cref="RelayReadKey"/>).</summary>
	public static bool IsRelayRead(JsonObject? meta) =>
		meta?[RelayReadKey] is JsonValue value && value.TryGetValue(out bool marked) && marked;

	private static readonly AsyncLocal<(string? Id, string? Client)> Ambient = new();

	/// <summary>
	/// Which client a call comes from: the transport's own session where it has one, else the one a relay named
	/// in <c>_meta</c>, else none.
	/// <para>
	/// The relay's name is needed because the http transport serves each request without a session -- the
	/// current protocol revision has none to give -- so every relayed session would otherwise be the same null
	/// one. It is an opaque id the relay mints for the life of its process, and it grants nothing: it decides only
	/// which of the broker's per-client state a call reaches, its own acknowledged reasons. A client that sends
	/// another's id has to know it, and nothing the broker answers carries one. It is read here, inside a tool
	/// call, which every request reaches only past the host's origin and token checks. Kept apart from any
	/// transport id by its prefix, and refused unless it has the shape a relay mints, since whatever is read here
	/// is compared and logged.
	/// </para>
	/// </summary>
	public static string? Of(string? transportSessionId, JsonObject? meta)
	{
		if (!string.IsNullOrWhiteSpace(transportSessionId)) return transportSessionId;

		if (meta?[MetaKey] is not JsonValue value || !value.TryGetValue(out string? named)) return null;

		return Relayed(named);
	}

	/// <summary>
	/// The client a relay's name for its session stands for -- <c>relayed-</c> and the name -- or null where the
	/// name is not in the shape <see cref="Mint"/> gives, since whatever is read here is compared and logged.
	/// </summary>
	public static string? Relayed(string? named)
	{
		var wellFormed = named is { Length: >= 16 and <= 64 } && named.All(char.IsAsciiHexDigitLower);

		return wellFormed ? $"relayed-{named}" : null;
	}

	/// <summary>A fresh id for a relay to name its session by, in the shape <see cref="Of"/> accepts.</summary>
	public static string Mint() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

	/// <summary>The calling session's id as the transport gives it, or null when the transport has no notion of one. Owns debug sessions.</summary>
	public static string? Id => Ambient.Value.Id;

	/// <summary>
	/// The calling client: <see cref="Id"/> where there is one, else the name its relay sent (<see cref="Of"/>).
	/// Owns acknowledged reasons.
	/// </summary>
	public static string? Client => Ambient.Value.Client;

	/// <summary>
	/// Sets both for the current call, to one id: a caller with a transport session is that client. Each MCP
	/// request runs on its own execution context, so this is scoped to one request and cannot leak into a
	/// concurrent one; it is restored anyway, because a filter that leaves ambient state behind is a filter
	/// nobody can reason about.
	/// </summary>
	public static IDisposable Use(string? id) => new Scope(id, id);

	/// <summary>Sets the transport's session and the client separately, for a call a relay named.</summary>
	public static IDisposable Use(string? id, string? client) => new Scope(id, client);

	private sealed class Scope : IDisposable
	{
		private readonly (string? Id, string? Client) _previous;

		public Scope(string? id, string? client)
		{
			_previous = Ambient.Value;
			Ambient.Value = (Blank(id) ? null : id, Blank(client) ? null : client);
		}

		public void Dispose() => Ambient.Value = _previous;

		private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
	}
}
