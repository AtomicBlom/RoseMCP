namespace RoseMcp.Contracts;

/// <summary>
/// What a stopping-breakpoint removal did with each id it was given, and the set left afterwards: the
/// breakpoint twin of <see cref="LiveTracepointRemoval"/>. Removing the breakpoint a target is held
/// at does not resume it.
/// </summary>
public sealed record LiveBreakpointRemoval : LiveResult
{
	/// <summary>How many of the ids named a breakpoint that is now gone.</summary>
	public int Removed { get; init; }

	/// <summary>How many ids were given.</summary>
	public int Total => Results.Count;

	public IReadOnlyList<LiveRemovalOutcome> Results { get; init; } = [];

	/// <summary>The stopping breakpoints the session still holds.</summary>
	public IReadOnlyList<LiveBreakpoint> Breakpoints { get; init; } = [];
}
