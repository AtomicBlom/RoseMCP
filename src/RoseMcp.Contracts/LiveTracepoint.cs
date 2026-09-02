namespace RoseMcp.Contracts;

/// <summary>
/// A tracepoint: a breakpoint that logs and auto-continues, never pausing the target. It is the
/// low-friction default for a turn-based agent, since it cannot freeze the app the way a stopping
/// breakpoint would. Each hit appears in the debug event stream as a
/// <see cref="LiveDebugEventKind.BreakpointHit"/> event.
/// </summary>
public sealed record LiveTracepoint
{
	/// <summary>The id the session assigned; pass it back to remove the tracepoint.</summary>
	public required string Id { get; init; }

	/// <summary>The location as requested, e.g. <c>MyApp.Widget.Refresh</c>.</summary>
	public required string Location { get; init; }

	/// <summary>Whether it is bound to a loaded method yet. An unbound one binds when its module loads.</summary>
	public required bool Bound { get; init; }

	/// <summary>How many times it has been hit so far.</summary>
	public required long HitCount { get; init; }

	/// <summary>An optional message logged on each hit that is not filtered out.</summary>
	public string? LogMessage { get; init; }

	/// <summary>When set, only every Nth hit is logged; every hit is still counted.</summary>
	public int? LogEveryNthHit { get; init; }

	/// <summary>Why it is not bound yet, when it is not (module not loaded, method not found).</summary>
	public string? Detail { get; init; }
}
