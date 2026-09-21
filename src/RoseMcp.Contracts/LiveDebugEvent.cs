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

	/// <summary>
	/// The managed call stack, innermost first, for an event captured while a thread was stopped (an
	/// exception or a stopping breakpoint). Each entry is a resolved <c>Namespace.Type.Method</c>;
	/// frames that could not be resolved are left out. Null when no stack was captured.
	/// </summary>
	public IReadOnlyList<string>? Frames { get; init; }

	/// <summary>
	/// The top frame's arguments and locals, captured at a stopping breakpoint. Null for events where
	/// no frame was inspected (exceptions, tracepoints, session notices).
	/// </summary>
	public IReadOnlyList<LiveVariable>? Variables { get; init; }

	/// <summary>
	/// The values a tracepoint's message named, one per placeholder, in the order they appear in it.
	/// Null for every other event, and for a tracepoint whose message interpolates nothing.
	/// <para>
	/// A field of its own rather than <see cref="Variables"/>, which is the whole top frame: these
	/// are what somebody asked to be shown and nothing else, and one field meaning "everything in
	/// scope" on one event and "the four things asked for" on another is a field that cannot be read
	/// without first knowing which kind of event carried it.
	/// </para>
	/// <para>
	/// They are here as well as inside <see cref="Message"/> because a value in a sentence cannot be
	/// read back. A page of hits is long enough to be truncated by the client displaying it, and the
	/// answer to that is to ask for one event by its sequence and read its fields -- which needs the
	/// fields to exist. A value that could not be read carries the reason as its value, in angle
	/// brackets, and no type.
	/// </para>
	/// </summary>
	public IReadOnlyList<LiveVariable>? Logged { get; init; }
}
