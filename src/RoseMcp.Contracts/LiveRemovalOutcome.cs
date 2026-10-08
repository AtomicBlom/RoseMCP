namespace RoseMcp.Contracts;

/// <summary>
/// One id a removal was given and what became of it. <see cref="Status"/> is <c>removed</c>,
/// <c>not found</c> for an id the session does not hold -- already removed, or never issued -- or
/// <c>refused: </c> and the reason, which is an id of the other kind: a tracepoint id given to the
/// breakpoint removal or the reverse.
/// </summary>
public sealed record LiveRemovalOutcome
{
	public required string Id { get; init; }

	public required string Status { get; init; }
}
