namespace RoseMcp.Contracts;

/// <summary>
/// What every answer a live-app session produces carries: where that session's event stream stood at
/// the moment the answer was produced.
/// <para>
/// A turn-based agent asking "did my action cause this" is asking about a position rather than about
/// a clock. A wait is answered by anything past the cursor it was given, so a cursor taken before the
/// action is the only one that cannot be answered out of history -- and a caller that has to go and
/// fetch one has a gap between fetching it and acting, and a rule to remember. Handing it back from
/// the call that acted closes both: the right cursor is already in the answer, and nobody computes
/// one.
/// </para>
/// <para>
/// Zero is the beginning of the stream, and it is the right answer exactly once -- at a session's
/// birth, where everything the target has ever done is also everything it has done since you
/// attached. Anywhere else, zero is a cursor nobody was handed.
/// </para>
/// </summary>
public abstract record LiveResult
{
	/// <summary>
	/// The newest event sequence the session had recorded when this answer was produced. Pass it as
	/// <c>after</c> to see only what happened next.
	/// <para>
	/// The same numbering as <see cref="LiveDebugEventPage.NextCursor"/> and
	/// <see cref="LiveStop.EventSequence"/>, which count the same events. It is not the sequence of
	/// any particular event: a stop says which event announced it, and this says what the stream had
	/// seen by the time the answer left.
	/// </para>
	/// </summary>
	public long Cursor { get; init; }
}
