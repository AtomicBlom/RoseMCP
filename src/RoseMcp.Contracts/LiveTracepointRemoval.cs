namespace RoseMcp.Contracts;

/// <summary>
/// What a tracepoint removal did with each id it was given, and the set left afterwards.
/// <see cref="Results"/> is one entry per id, in order; an id the session does not hold is that
/// entry's status and never stops the others. <see cref="Tracepoints"/> is what the session holds
/// once the call is done, so a caller learns what is still logging without a second call.
/// </summary>
public sealed record LiveTracepointRemoval : LiveResult
{
	/// <summary>How many of the ids named a tracepoint that is now gone.</summary>
	public int Removed { get; init; }

	/// <summary>How many ids were given.</summary>
	public int Total => Results.Count;

	public IReadOnlyList<LiveRemovalOutcome> Results { get; init; } = [];

	/// <summary>The tracepoints the session still holds.</summary>
	public IReadOnlyList<LiveTracepoint> Tracepoints { get; init; } = [];
}
