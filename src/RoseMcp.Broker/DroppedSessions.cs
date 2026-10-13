namespace RoseMcp.Broker;

/// <summary>
/// The live-app sessions the manager dropped because their host died, remembered so a later call
/// naming one hears why it is gone rather than a refusal that reads as a wrong id or another client's
/// session.
/// <para>
/// Bounded by count, oldest forgotten first: a broker running for weeks drops sessions for weeks, and
/// an id old enough to fall off the end is one nobody is still holding. Session ids are never reused,
/// so a remembered id can never be confused with a live one.
/// </para>
/// <para>
/// Scoped by owner the way <see cref="LiveAppSessionManager.Find"/> is: that a session existed and
/// what it was debugging is not another client's business, so a caller who was not one of its owners
/// is told nothing more than if it had never existed. Every owner is told, the agent session a person
/// shared it with as much as the one that started it.
/// </para>
/// </summary>
public sealed class DroppedSessions(int capacity = DroppedSessions.DefaultCapacity)
{
	/// <summary>How many dropped sessions are remembered by default.</summary>
	public const int DefaultCapacity = 64;

	private readonly Lock _gate = new();
	private readonly LinkedList<DroppedSession> _dropped = [];

	/// <summary>Remembers a dropped session, forgetting the oldest once past the capacity.</summary>
	public void Record(DroppedSession dropped)
	{
		lock (_gate)
		{
			_dropped.AddLast(dropped);

			while (_dropped.Count > capacity) _dropped.RemoveFirst();
		}
	}

	/// <summary>The dropped session with that id, if the call from <paramref name="transportSession"/>, filed under <paramref name="clientKey"/>, was one of its owners.</summary>
	public DroppedSession? Find(string sessionId, string? transportSession, string? clientKey)
	{
		var dropped = ForOperator(sessionId);

		return dropped is not null && dropped.Owners.Admits(transportSession, clientKey) ? dropped : null;
	}

	/// <summary>
	/// The dropped session with that id, whoever started it, for the person running the broker -- the
	/// counterpart of <see cref="LiveAppSessionManager.ForOperator"/>.
	/// </summary>
	public DroppedSession? ForOperator(string sessionId)
	{
		lock (_gate)
		{
			return _dropped.FirstOrDefault(dropped => string.Equals(dropped.SessionId, sessionId, StringComparison.Ordinal));
		}
	}
}
