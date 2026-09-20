namespace RoseMcp.Contracts;

/// <summary>A session's tracepoints. A wrapper so the result is always a structured object.</summary>
public sealed record LiveTracepointList : LiveResult
{
	public IReadOnlyList<LiveTracepoint> Tracepoints { get; init; } = [];
}
