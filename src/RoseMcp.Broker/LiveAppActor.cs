namespace RoseMcp.Broker;

/// <summary>
/// Who did something to a debug session: the person, through the operator API, or the agent session the client table
/// files under <paramref name="ClientKey"/>. Recorded where the broker sees the act, since the host it reaches never
/// learns who asked.
/// </summary>
/// <param name="ClientKey">The calling session's key in the client table (<see cref="CallSession.ClientKey"/>); null for the person.</param>
public sealed record LiveAppActor(string? ClientKey)
{
	/// <summary>The person at a window, who reaches a session only through the operator API.</summary>
	public static LiveAppActor Person { get; } = new((string?)null);

	/// <summary>The agent session whose MCP call is in flight.</summary>
	public static LiveAppActor OfCaller() => new(CallSession.ClientKey);

	/// <summary>Whether it was the person rather than an agent session.</summary>
	public bool IsPerson => ClientKey is null;
}
