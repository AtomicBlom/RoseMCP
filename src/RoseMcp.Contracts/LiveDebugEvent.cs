namespace RoseMcp.Contracts;

/// <summary>
/// One thing that happened in the debuggee, captured by the live-app host and buffered for a
/// turn-based agent to read when it next looks. Every event carries a monotonic
/// <see cref="Sequence"/> so a reader can ask for only what is new and can tell when it missed some.
/// </summary>
public sealed record LiveDebugEvent
{
	/// <summary>Monotonic, assigned by the host in arrival order. Never reused within a session.</summary>
	public required long Sequence { get; init; }

	public required DateTime TimestampUtc { get; init; }

	public required LiveDebugEventKind Kind { get; init; }

	/// <summary>A one-line, human-readable summary, already formed by the host.</summary>
	public required string Message { get; init; }

	/// <summary>The debuggee thread this happened on, where the callback carried one.</summary>
	public int? ThreadId { get; init; }

	/// <summary>The module, for a load event.</summary>
	public string? ModuleName { get; init; }

	/// <summary>The exception's type name, for an exception event, where it could be decoded.</summary>
	public string? ExceptionType { get; init; }
}
