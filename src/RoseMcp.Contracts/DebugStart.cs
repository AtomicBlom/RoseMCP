namespace RoseMcp.Contracts;

/// <summary>
/// What a person's attach or launch answers: the session that is debugging the target, and whether it was already
/// open -- a process has one debugger, so attaching to one that is being debugged opens that session rather than
/// starting a second.
/// </summary>
public sealed record DebugStart
{
	/// <summary>The session debugging the target, belonging to the person where this request started it.</summary>
	public required LiveAppSessionSummary Session { get; init; }

	/// <summary>Whether the session was already open rather than started by this request.</summary>
	public bool Existing { get; init; }
}
