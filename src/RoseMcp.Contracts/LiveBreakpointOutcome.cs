namespace RoseMcp.Contracts;

/// <summary>
/// One requested stopping breakpoint and what became of it, in the four statuses
/// <see cref="LiveTracepointOutcome"/> uses with <c>set</c> for <c>added</c>: <c>set</c>,
/// <c>set, not bound yet</c> and the reason, <c>set, will not bind</c> and the reason, or
/// <c>refused: </c> and the reason. Only a set one carries <see cref="Breakpoint"/>, whose id is what
/// removes it.
/// </summary>
public sealed record LiveBreakpointOutcome
{
	/// <summary>The location as requested, which is how an entry is matched back to its request.</summary>
	public required string Location { get; init; }

	public required string Status { get; init; }

	/// <summary>The breakpoint as the session now holds it, or null when it was refused.</summary>
	public LiveBreakpoint? Breakpoint { get; init; }
}
