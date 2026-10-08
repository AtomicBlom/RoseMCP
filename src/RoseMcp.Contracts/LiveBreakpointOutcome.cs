namespace RoseMcp.Contracts;

/// <summary>
/// One requested stopping breakpoint and what became of it. <see cref="Status"/> is <c>set</c> when it
/// bound, <c>set, not bound yet: </c> and the reason when it is waiting for its module, or
/// <c>refused: </c> and the reason when it was not set at all. Only a set one carries
/// <see cref="Breakpoint"/>, whose id is what removes it.
/// </summary>
public sealed record LiveBreakpointOutcome
{
	/// <summary>The location as requested, which is how an entry is matched back to its request.</summary>
	public required string Location { get; init; }

	public required string Status { get; init; }

	/// <summary>The breakpoint as the session now holds it, or null when it was refused.</summary>
	public LiveBreakpoint? Breakpoint { get; init; }
}
