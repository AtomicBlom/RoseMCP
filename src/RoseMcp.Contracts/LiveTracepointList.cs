namespace RoseMcp.Contracts;

/// <summary>A session's tracepoints. A wrapper so the result is always a structured object.</summary>
public sealed record LiveTracepointList
{
	public IReadOnlyList<LiveTracepoint> Tracepoints { get; init; } = [];
}
