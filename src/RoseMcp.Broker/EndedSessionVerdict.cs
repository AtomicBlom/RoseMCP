namespace RoseMcp.Broker;

/// <summary>What the session manager does about one live-app session on a tick of its poll.</summary>
public enum EndedSessionVerdict
{
	/// <summary>Leave it: its host answers, or it ended too recently to drop.</summary>
	Keep,

	/// <summary>Its host has just been found gone: record when, and say so on its row, which stays for the grace period.</summary>
	MarkEnded,

	/// <summary>Its host has been gone past the grace period: take it out of the registry and end what is left of it.</summary>
	Drop,
}
