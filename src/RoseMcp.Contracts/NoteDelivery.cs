namespace RoseMcp.Contracts;

/// <summary>
/// Where a note to an agent has got to.
/// <para>
/// A note is delivered by riding on one answer to its client's next call, so the states are the ones that route
/// can be in. A push to a session that listens would sit between <see cref="Waiting"/> and
/// <see cref="Delivered"/> as a state of its own, since nothing confirms a pushed note arrived until the session's
/// next call or reply does; it is added there when there is a push, and a reader meeting a value it does not know
/// treats it as waiting.
/// </para>
/// </summary>
public enum NoteDelivery
{
	/// <summary>Queued for its client, whose session is still connected: it rides on that session's next answer.</summary>
	Waiting,

	/// <summary>Handed to the client in the answer to one of its calls, or confirmed by its reply.</summary>
	Delivered,

	/// <summary>
	/// Queued for a client the broker no longer lists, because its session went before its next call. It is still
	/// delivered if that session calls again, and kept to be read for a day after its session went, or after the last
	/// thing that happened to it where that is later.
	/// </summary>
	ClientGone,
}
