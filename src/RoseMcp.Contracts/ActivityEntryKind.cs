namespace RoseMcp.Contracts;

/// <summary>What an <see cref="ActivityEntry"/> records.</summary>
public enum ActivityEntryKind
{
	/// <summary>One tool call a client made, from when it arrived to when it was answered.</summary>
	Call,

	/// <summary>
	/// A broker starting. Written once per broker run, before its first call, so a reader sees a restart as a
	/// line saying so rather than as a gap between two calls that could as well be a quiet hour.
	/// </summary>
	BrokerStarted,
}
