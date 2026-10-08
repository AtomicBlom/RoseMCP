namespace RoseMcp.Contracts;

/// <summary>
/// One requested tracepoint and what became of it. <see cref="Status"/> is one of four:
/// <c>added</c> when it bound; <c>added, not bound yet</c> and the reason when it is waiting for its
/// module to load, and binds when it does; <c>added, will not bind</c> and the reason when the module
/// that would carry it is loaded and cannot -- no such method or type, a type several modules
/// declare, a bind the runtime refused -- so it should be removed and asked for again; or
/// <c>refused: </c> and the reason when it was not added at all. Only an added one carries
/// <see cref="Tracepoint"/>, whose id is what removes it.
/// </summary>
public sealed record LiveTracepointOutcome
{
	/// <summary>The location as requested, which is how an entry is matched back to its request.</summary>
	public required string Location { get; init; }

	public required string Status { get; init; }

	/// <summary>The tracepoint as the session now holds it, or null when it was refused.</summary>
	public LiveTracepoint? Tracepoint { get; init; }
}
