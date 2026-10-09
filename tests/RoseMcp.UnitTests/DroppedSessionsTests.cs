using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the session manager remembers about a session it dropped because its host died: enough for
/// the caller that started it to hear why its id stopped working, nothing for anyone else, and only
/// so many of them.
/// </summary>
public sealed class DroppedSessionsTests
{
	private static readonly DateTime Gone = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

	private static DroppedSession Dropped(string id, string? owner = "mcp-one") => new(id, owner, "probe target", Gone);

	/// <summary>The caller that started the session is told it was dropped, and why.</summary>
	[Test]
	public void The_owner_finds_a_dropped_session()
	{
		var dropped = new DroppedSessions();
		dropped.Record(Dropped("session-a"));

		dropped.Find("session-a", "mcp-one").ShouldNotBeNull().Target.ShouldBe("probe target");
	}

	/// <summary>
	/// Another client hears nothing more than it would about an id that never existed: that a session
	/// existed, and what it was debugging, is not its business.
	/// </summary>
	[Test]
	public void Another_client_does_not_find_a_dropped_session()
	{
		var dropped = new DroppedSessions();
		dropped.Record(Dropped("session-a"));

		dropped.Find("session-a", "mcp-two").ShouldBeNull();
		dropped.Find("session-a", owner: null).ShouldBeNull();
	}

	/// <summary>The person running the broker sees every dropped session, as they see every open one.</summary>
	[Test]
	public void The_operator_finds_a_dropped_session_whoever_started_it() =>
		WithOne(Dropped("session-a")).ForOperator("session-a").ShouldNotBeNull();

	/// <summary>A broker that drops sessions for weeks remembers only the latest, oldest forgotten first.</summary>
	[Test]
	public void Only_the_latest_dropped_sessions_are_remembered()
	{
		var dropped = new DroppedSessions(capacity: 2);

		dropped.Record(Dropped("session-a"));
		dropped.Record(Dropped("session-b"));
		dropped.Record(Dropped("session-c"));

		dropped.ForOperator("session-a").ShouldBeNull();
		dropped.ForOperator("session-b").ShouldNotBeNull();
		dropped.ForOperator("session-c").ShouldNotBeNull();
	}

	/// <summary>The refusal says the id was right, that the host died and when, and what to do instead.</summary>
	[Test]
	public void The_reason_names_the_session_the_dead_host_and_what_next()
	{
		var reason = Dropped("session-a").Explain(Gone + TimeSpan.FromMinutes(3), "Start a new session.");

		reason.ShouldContain("'session-a'");
		reason.ShouldContain("host stopped answering 3 min ago");
		reason.ShouldEndWith("Start a new session.");
	}

	private static DroppedSessions WithOne(DroppedSession session)
	{
		var dropped = new DroppedSessions();
		dropped.Record(session);
		return dropped;
	}
}
