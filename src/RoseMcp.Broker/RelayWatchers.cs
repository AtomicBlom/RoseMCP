namespace RoseMcp.Broker;

/// <summary>
/// Which relays are watching the broker, and when the ones that have stopped let go: one reading of
/// <see cref="RelayConnections"/>, taken at once.
/// </summary>
/// <param name="Watching">The relays watching now, by id, each with the process it said it is.</param>
/// <param name="Stopped">When each relay watching no longer last let go of its watch, for an hour after.</param>
public sealed record RelayWatchers(
	IReadOnlyDictionary<string, int?> Watching,
	IReadOnlyDictionary<string, DateTimeOffset> Stopped)
{
	/// <summary>Whether relay <paramref name="relayId"/> is watching now.</summary>
	public bool IsWatching(string relayId) => Watching.ContainsKey(relayId);

	/// <summary>When relay <paramref name="relayId"/> let go of its watch, where it has and that is still remembered.</summary>
	public DateTimeOffset? StoppedUtc(string relayId) => Stopped.TryGetValue(relayId, out var stopped) ? stopped : null;
}
