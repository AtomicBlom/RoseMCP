namespace RoseMcp.Broker;

/// <summary>
/// Decides when a live-app session whose host has gone is dropped, and says why in words a person
/// reads on its row before it goes.
/// <para>
/// Only a host that has stopped answering ends a session here. A host that is alive and reports its
/// target as exited is left alone: its event log is still readable, which is what a caller who
/// watched a process die wants to read next, and closing it is that caller's act. A dead host leaves
/// nothing to read, and nothing but this would ever remove it -- the registry would carry it, and
/// the poll would ask it how it is every second, for the life of the broker.
/// </para>
/// <para>
/// A pure function over the facts and the clock, apart from the manager that acts on it, so the rule
/// is tested without killing a host and waiting out a real grace period.
/// </para>
/// </summary>
public static class EndedSessionEviction
{
	/// <summary>
	/// The verdict for one session. A host found gone is marked first and dropped only once it has
	/// been gone for <paramref name="grace"/>, so every reader polling the registry -- the tray, an
	/// inspector, <c>rose_debug_list</c> -- can see the session as ended, and why, before it vanishes.
	/// </summary>
	/// <param name="hostAnswers">Whether the session's host is still there to answer.</param>
	/// <param name="endedSeenUtc">When the manager first found the host gone, or null if it has not.</param>
	/// <param name="grace">How long an ended session stays listed.</param>
	/// <param name="nowUtc">Now, on the manager's clock.</param>
	public static EndedSessionVerdict Decide(bool hostAnswers, DateTime? endedSeenUtc, TimeSpan grace, DateTime nowUtc)
	{
		if (hostAnswers) return EndedSessionVerdict.Keep;

		if (endedSeenUtc is not { } seen) return EndedSessionVerdict.MarkEnded;

		return nowUtc - seen >= grace ? EndedSessionVerdict.Drop : EndedSessionVerdict.Keep;
	}

	/// <summary>
	/// Why an ended session is about to go, for its row in the activity log. Written for whoever finds
	/// it ended, so it says what follows as well as what happened.
	/// </summary>
	public static string Explain(TimeSpan grace) =>
		"The live-app host stopped answering, so the debugger is no longer on the target and nothing in "
			+ $"this session can be read. It is dropped from the list in {WorkerEviction.Duration(grace)}; "
			+ "start a new session to debug the target again.";
}
