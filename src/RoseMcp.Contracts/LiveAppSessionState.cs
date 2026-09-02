namespace RoseMcp.Contracts;

/// <summary>Lifecycle of one live-app session, as reported to callers and the admin view.</summary>
public enum LiveAppSessionState
{
	/// <summary>The host process is starting and has not yet established the target.</summary>
	Starting,

	/// <summary>The host is attached to (or has launched) the target and is serving requests.</summary>
	Ready,

	/// <summary>The host could not establish the target, or the host process died.</summary>
	Faulted,

	/// <summary>The session was closed and its host has exited.</summary>
	Ended,
}
