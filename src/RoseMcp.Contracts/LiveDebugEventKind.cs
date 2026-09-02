namespace RoseMcp.Contracts;

/// <summary>
/// What a <see cref="LiveDebugEvent"/> reports. These mirror the ICorDebug managed callbacks the
/// host listens to, plus a session-level note the host raises itself (attached, detached, faulted).
/// </summary>
public enum LiveDebugEventKind
{
	/// <summary>The host raised this itself: attached, detached, or a problem with the session.</summary>
	SessionNotice,

	/// <summary>The debuggee's process was reported to the debugger (fires once, on attach).</summary>
	ProcessCreated,

	/// <summary>The debuggee exited. No further events follow.</summary>
	ProcessExited,

	ModuleLoaded,

	ThreadCreated,

	ThreadExited,

	/// <summary>An exception was thrown; it may still be caught. Decoded to its type where possible.</summary>
	ExceptionFirstChance,

	/// <summary>An exception went unhandled and is about to tear the process down.</summary>
	ExceptionUnhandled,

	/// <summary>A <c>System.Diagnostics.Debugger.Log</c> message from the debuggee.</summary>
	LogMessage,

	/// <summary>Execution reached a breakpoint the debugger set (a tracepoint hit, or a stopping hold).</summary>
	BreakpointHit,

	/// <summary>A step (in/over/out) finished and the target is held at the new location.</summary>
	StepComplete,
}
