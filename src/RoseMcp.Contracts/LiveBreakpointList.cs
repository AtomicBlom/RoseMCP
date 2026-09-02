namespace RoseMcp.Contracts;

/// <summary>A session's stopping breakpoints. A wrapper so the result is always a structured object.</summary>
public sealed record LiveBreakpointList
{
	public IReadOnlyList<LiveBreakpoint> Breakpoints { get; init; } = [];
}
