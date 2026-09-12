namespace RoseMcp.Contracts;

/// <summary>
/// The outcome of ending a debug session.
/// <para>
/// Two claims, kept apart on purpose: whether the session is closed, and whether the debugger is
/// actually off the target. They are not the same, and only the second is what somebody detaching
/// asked for -- a close whose detach failed leaves an app being watched by a debugger nothing is
/// driving, which is worse than either a clean detach or a clean failure.
/// </para>
/// </summary>
public sealed record LiveSessionClosed
{
	/// <summary>False when there was no such session to close.</summary>
	public required bool Closed { get; init; }

	/// <summary>
	/// Why the debugger could not be detached, when it could not. Null means the target was left
	/// running with nothing attached to it, which is what a detach is for.
	/// </summary>
	public string? DetachFailure { get; init; }
}
