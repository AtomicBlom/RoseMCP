namespace RoseMcp.Contracts;

/// <summary>
/// What <c>rose_debug_detach</c> did, and to which session.
/// <para>
/// A sentence saying a session was detached names none, which is the one thing a caller holding
/// several cannot check, and gives an agent nothing to branch on but the wording. The session id is
/// the one the caller passed rather than one read back from the session, because a session that was
/// not open has nothing to read it from and is still the session the answer is about.
/// </para>
/// <para>
/// No cursor, unlike a <see cref="LiveResult"/>. A cursor is a position in the session's event
/// stream, to be passed back to read what follows, and the stream ends with the session; a number
/// here would invite a read nothing can answer.
/// </para>
/// <para>
/// A detach that closed the session but could not take the debugger off the target is not answered
/// with this at all. It is refused with the reason, since it is the one outcome where the target is at
/// risk, and a result whose fields said so could be read past.
/// </para>
/// </summary>
public sealed record LiveSessionDetached
{
	/// <summary>The session the detach was asked of.</summary>
	public required string SessionId { get; init; }

	/// <summary>
	/// Whether there was an open session to detach. False is not a failure: it says the session was
	/// already closed, or never existed, which is what makes detaching twice harmless. True means the
	/// session is closed and the target was left running with nothing attached to it.
	/// </summary>
	public required bool Detached { get; init; }
}
