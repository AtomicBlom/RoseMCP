namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// What a debug session's target is doing, as one value rather than a set of flags that can
/// disagree with each other.
/// <para>
/// The states are exclusive and that is the point: a target cannot be both stopped and gone, so a
/// reader cannot be told it is stopped at a breakpoint by one field and that the process has ended
/// by another. Every guard is a pattern match, so a state added here is a state the compiler makes
/// each guard account for -- which is what stops a new verb from inventing a sixth spelling of
/// "is there anything live to talk to".
/// </para>
/// <para>
/// The session swaps this whole value under its gate, but mscordbi's callback thread reads it
/// without one -- a detach in progress has to be seen from there while the detaching thread holds
/// the gate. A reference read is atomic, which is what makes that safe; mutating a state in place
/// would not be.
/// </para>
/// </summary>
internal abstract record TargetExecution
{
	private TargetExecution()
	{
	}

	/// <summary>The target is executing. Frames, locals and threads are not readable.</summary>
	internal sealed record Running : TargetExecution;

	/// <summary>
	/// The target is held at a breakpoint, a step or an operator's pause, and <paramref name="Held"/>
	/// is the stop being held -- the only state in which there is one.
	/// </summary>
	internal sealed record Stopped(StopRecord Held) : TargetExecution;

	/// <summary>
	/// A detach is stepping the target off a breakpoint patch. In that window the target runs with
	/// its breakpoints still live, so a callback can arrive and must be continued without the gate,
	/// which the detaching thread is holding.
	/// </summary>
	internal sealed record Detaching : TargetExecution;

	/// <summary>
	/// The debugger has let the target go. It keeps running, and the debugging interface is safe to
	/// terminate.
	/// </summary>
	internal sealed record Detached : TargetExecution;

	/// <summary>The target process has ended. Nothing is left to continue, stop or read.</summary>
	internal sealed record Exited : TargetExecution;

	/// <summary>
	/// The stop being held, or null in every other state. The one place that decides whether frames,
	/// locals and threads can be asked for at all.
	/// </summary>
	internal StopRecord? Stop => this is Stopped stopped ? stopped.Held : null;

	/// <summary>
	/// Whether there is still a target to talk to: it has neither gone nor been let go, and no detach
	/// is part-way through handing it back.
	/// </summary>
	internal bool IsLive => this is Running or Stopped;
}
