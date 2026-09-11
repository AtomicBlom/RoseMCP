namespace RoseMcp.Contracts;

/// <summary>
/// A stopping breakpoint: on hit it holds the target and records the stop with its stack, so an agent
/// can inspect it, then resumes when told to continue -- or on its own after a safety timeout, so an
/// unattended stop cannot wedge the app. Its non-pausing sibling is the <see cref="LiveTracepoint"/>.
/// </summary>
public sealed record LiveBreakpoint
{
	/// <summary>The id the session assigned; pass it back to remove the breakpoint.</summary>
	public required string Id { get; init; }

	/// <summary>The location as requested, e.g. <c>MyApp.Widget.Refresh</c>.</summary>
	public required string Location { get; init; }

	/// <summary>Always true for a breakpoint; the field marks it apart from a tracepoint in a listing.</summary>
	public required bool StopOnHit { get; init; }

	/// <summary>Whether it is bound to a loaded method yet. An unbound one binds when its module loads.</summary>
	public required bool Bound { get; init; }

	/// <summary>
	/// The instruction it was asked to stop at inside the method, when a position was picked rather
	/// than the method named. Null means its first instruction.
	/// </summary>
	public int? IlOffset { get; init; }

	/// <summary>
	/// Where in source it actually bound, once it has. Null while unbound, or when the module has no
	/// symbols to say.
	/// <para>
	/// It is the answer to the question a location string cannot settle. A location names a method
	/// and two overloads share one, so a breakpoint set by name binds to whichever the metadata lists
	/// first -- and the file and line it reports is how somebody sees that it went somewhere other
	/// than where they meant.
	/// </para>
	/// </summary>
	public LiveSourcePosition? Source { get; init; }

	/// <summary>How many times it has been hit so far.</summary>
	public required long HitCount { get; init; }

	/// <summary>Seconds a hit is held before the target auto-continues if nothing resumes it sooner.</summary>
	public required int AutoContinueSeconds { get; init; }

	/// <summary>A cheap value-compare condition (<c>name OP literal</c>) that gates each hit, if any.</summary>
	public string? Condition { get; init; }

	/// <summary>Why it is not bound yet, when it is not (module not loaded, method not found).</summary>
	public string? Detail { get; init; }
}
