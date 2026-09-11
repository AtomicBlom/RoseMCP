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

	/// <summary>
	/// The instruction it was asked to log at inside the method, when a position was picked rather
	/// than the method named. Null means its first instruction.
	/// </summary>
	public int? IlOffset { get; init; }

	/// <summary>
	/// Where in source it actually bound, once it has. Null while unbound, or when the module has no
	/// symbols to say. A location names a method and two overloads share one, so this is how somebody
	/// sees that it went somewhere other than where they meant.
	/// </summary>
	public LiveSourcePosition? Source { get; init; }

	/// <summary>How many times it has been hit so far.</summary>
	public required long HitCount { get; init; }

	/// <summary>An optional message logged on each hit that is not filtered out.</summary>
	public string? LogMessage { get; init; }

	/// <summary>When set, only every Nth hit is logged; every hit is still counted.</summary>
	public int? LogEveryNthHit { get; init; }

	/// <summary>A cheap value-compare condition (<c>name OP literal</c>) that gates each hit, if any.</summary>
	public string? Condition { get; init; }

	/// <summary>Why it is not bound yet, when it is not (module not loaded, method not found).</summary>
	public string? Detail { get; init; }
}
