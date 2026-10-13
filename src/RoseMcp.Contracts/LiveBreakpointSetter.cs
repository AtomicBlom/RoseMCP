namespace RoseMcp.Contracts;

/// <summary>
/// Who set a breakpoint: the person, from a window through the operator API, or an agent session through
/// <c>rose_debug_set_breakpoint</c>. The broker records it as the breakpoint is set, since the host that holds the
/// breakpoint never learns who asked for it.
/// </summary>
public sealed record LiveBreakpointSetter
{
	/// <summary>Whether the person set it, from a window.</summary>
	public required bool Person { get; init; }

	/// <summary>
	/// The agent session that set it, where an agent did and the client table still lists it; null for the person,
	/// and for an agent session the table no longer lists.
	/// </summary>
	public LiveAppAgent? Agent { get; init; }
}
