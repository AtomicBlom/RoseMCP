using ClrDebug;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// One breakpoint or tracepoint a caller asked for, and what became of it. It exists from the moment
/// somebody names a location, whether or not the module that would carry it is loaded, which is what
/// lets a location be set against a process that has not reached the code yet.
/// <para>
/// A binding is mutable on purpose: the same object goes from unbound with a reason, to bound at a
/// token in a module, and back to unbound when a detach releases it. A caller that listed it holds
/// the id, and the id has to keep meaning the same request across all of that.
/// </para>
/// <para>
/// Every member assumes the caller holds the session's gate. Nothing here locks.
/// </para>
/// </summary>
internal sealed class BreakpointBinding
{
	public required string Id { get; init; }

	public required SymbolLocation Location { get; init; }

	/// <summary>The location as the caller wrote it, for reporting.</summary>
	public required string Raw { get; init; }

	/// <summary>True for a stopping breakpoint; false for a tracepoint (log and continue).</summary>
	public required bool StopOnHit { get; init; }

	/// <summary>The message as the caller wrote it, placeholders and all, for reporting.</summary>
	public string? LogMessage { get; init; }

	/// <summary>
	/// The parsed message rendered on each hit; null when there is no message. Parsed once here
	/// rather than per hit, because a tracepoint on a hot method renders it thousands of times and
	/// the text it was written from cannot change.
	/// </summary>
	public LogMessageTemplate? LogTemplate { get; init; }

	public int? LogEveryNthHit { get; init; }

	public int? AutoContinueSeconds { get; init; }

	/// <summary>The condition as the caller wrote it, for reporting; null when there is none.</summary>
	public string? ConditionText { get; init; }

	/// <summary>The parsed condition evaluated on each hit; null when there is none.</summary>
	public BreakpointCondition? Condition { get; init; }

	public long HitCount { get; set; }

	/// <summary>The bound method's metadata token, used to match a hit back to this binding.</summary>
	public int? Token { get; set; }

	/// <summary>The module file it bound in, for reading the symbols that say where that was.</summary>
	public string? ModulePath { get; set; }

	/// <summary>
	/// Where in source it bound, read once at bind rather than on each listing: a listing is
	/// polled while a panel is open and the answer cannot change while the module is loaded.
	/// </summary>
	public LiveSourcePosition? Source { get; set; }

	public CorDebugFunctionBreakpoint? Breakpoint { get; set; }

	public string? Detail { get; set; }

	public bool Bound => Breakpoint is not null;
}
