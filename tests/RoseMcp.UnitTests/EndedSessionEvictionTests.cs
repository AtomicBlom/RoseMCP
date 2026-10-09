using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// When the session manager drops a live-app session. Only a host that has stopped answering ends
/// one, and it is listed as ended for a grace period first, so a reader polling the registry sees why
/// before the row goes.
/// </summary>
public sealed class EndedSessionEvictionTests
{
	private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
	private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

	/// <summary>
	/// A host that answers is kept whatever else is true of it -- including a host whose target has
	/// exited, whose event log is still there to read.
	/// </summary>
	[Test]
	public void A_session_whose_host_answers_is_kept() =>
		EndedSessionEviction.Decide(hostAnswers: true, endedSeenUtc: null, Grace, Now).ShouldBe(EndedSessionVerdict.Keep);

	/// <summary>The tick that first finds the host gone marks it, rather than dropping it unseen.</summary>
	[Test]
	public void A_host_just_found_gone_is_marked_rather_than_dropped() =>
		EndedSessionEviction.Decide(hostAnswers: false, endedSeenUtc: null, Grace, Now).ShouldBe(EndedSessionVerdict.MarkEnded);

	[Test]
	public void A_session_ended_within_the_grace_period_is_kept() =>
		EndedSessionEviction.Decide(hostAnswers: false, Now - TimeSpan.FromSeconds(29), Grace, Now).ShouldBe(EndedSessionVerdict.Keep);

	[Test]
	public void A_session_ended_past_the_grace_period_is_dropped() =>
		EndedSessionEviction.Decide(hostAnswers: false, Now - Grace, Grace, Now).ShouldBe(EndedSessionVerdict.Drop);

	/// <summary>
	/// The reason is read on the ended row by whoever finds it, so it says what happened, when the row
	/// goes, and what to do instead.
	/// </summary>
	[Test]
	public void The_reason_says_the_host_went_when_the_row_goes_and_what_next()
	{
		var reason = EndedSessionEviction.Explain(Grace);

		reason.ShouldContain("stopped answering");
		reason.ShouldContain("30 s");
		reason.ShouldContain("start a new session");
	}
}
