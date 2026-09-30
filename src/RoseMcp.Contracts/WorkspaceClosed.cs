namespace RoseMcp.Contracts;

/// <summary>
/// What closing a workspace did, and which workspace it was.
/// <para>
/// A sentence saying a workspace closed names none, which is the one thing a caller with several
/// open cannot check. The path and the key come from the resolution the close already performed, so
/// a caller who passed a hint learns which solution that hint chose -- the same question every other
/// result answers, and the reason a close cannot answer it with a string.
/// </para>
/// <para>
/// No revision. A revision identifies a snapshot of a loaded solution, and the point of this result
/// is that there is no longer one to identify; a zero would read as a snapshot rather than as an
/// absence.
/// </para>
/// </summary>
public sealed record WorkspaceClosed : WorkspaceScopedResult
{
	/// <summary>
	/// Whether a worker was running to be stopped. False is not a failure: it says the workspace
	/// was already closed, which is what makes calling this twice harmless.
	/// </summary>
	public required bool Closed { get; init; }
}
