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
/// what it was debugging is not another client's business, so a caller who did not start it is told
/// nothing more than if it had never existed.
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

	/// <summary>The dropped session with that id, if <paramref name="owner"/> started it.</summary>
	public DroppedSession? Find(string sessionId, string? owner)
	{
		var dropped = ForOperator(sessionId);

		return dropped is not null && string.Equals(dropped.Owner, owner, StringComparison.Ordinal) ? dropped : null;
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
