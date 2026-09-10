namespace RoseMcp.Contracts;

/// <summary>
/// Whether a debugged target is executing, and if not, what stopped it. Orthogonal to
/// <see cref="LiveAppSessionState"/>, which is the session's lifecycle: a session is
/// <see cref="LiveAppSessionState.Ready"/> whether its target is running or held.
/// <para>
/// Nothing reported this before, so the only way to tell a stopped target from a running one was to
/// read the event stream and reason about what had happened since -- which meant a status view could
/// not say, and a XAML request into a stopped target could only fail by timing out.
/// </para>
/// </summary>
public enum LiveExecutionState
{
	/// <summary>The target is executing. Frames, locals and threads are not readable.</summary>
	Running,

	/// <summary>Held at a stopping breakpoint, which names itself in <see cref="LiveStop.BreakpointId"/>.</summary>
	StoppedAtBreakpoint,

	/// <summary>Held at the end of a step, which no breakpoint owns.</summary>
	StoppedAtStep,
}
