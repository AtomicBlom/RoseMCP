using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the broker remembers about a workspace's health: when each reason was first seen, which the person
/// accepted and what agents read about them, what each session acknowledged, and what agents proposed.
/// <para>
/// The first two outlive every worker and every broker, so they are read back by a second instance from the
/// same folder, which is what a reload or a restart of the tray is. The last two are a session's, so they are
/// asked about from a session that did not make them, and from one that has gone quiet.
/// </para>
/// </summary>
public sealed class WorkspaceHealthTests
{
	private static readonly string Shop = Path.Combine(Path.GetTempPath(), "rose-health", "Shop.slnx");

	private static readonly HealthReason Legacy = Reason(ReasonKinds.ProjectNotLoaded, "Legacy.Tests");

	[Test]
	public void A_reason_is_dated_when_first_seen_and_keeps_that_date_across_loads_and_restarts()
	{
		using var home = new TemporaryLocalAppData();
		var clock = new SteerableClock(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));

		var first = Health(home, clock).Describe(Shop, [Legacy]).ShouldHaveSingleItem();
		first.FirstSeenUtc.ShouldBe(clock.Now);

		var seenAt = clock.Now;
		clock.Advance(TimeSpan.FromDays(3));

		// A fresh instance is a restarted broker, and a reason described again is the next load's.
		var later = Health(home, clock).Describe(Shop, [Legacy]).ShouldHaveSingleItem();
		later.FirstSeenUtc.ShouldBe(seenAt);
	}

	/// <summary>A reason that went and stayed gone a month is forgotten, so a removed worktree leaves nothing behind for ever.</summary>
	[Test]
	public void A_sighting_long_gone_is_forgotten()
	{
		using var home = new TemporaryLocalAppData();
		var clock = new SteerableClock(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
		var health = Health(home, clock);

		health.Describe(Shop, [Legacy]);
		clock.Advance(WorkspaceHealth.SeenRetention + TimeSpan.FromDays(1));

		var other = Reason(ReasonKinds.RestoreFailed);
		health.Describe(Shop, [other]);
		clock.Advance(TimeSpan.FromMinutes(1));

		health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().FirstSeenUtc.ShouldBe(clock.Now);
	}

	[Test]
	public void An_acceptance_is_remembered_across_restarts_and_says_what_agents_read()
	{
		using var home = new TemporaryLocalAppData();
		var clock = new SteerableClock(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));

		Health(home, clock).Accept(Shop, ReasonKinds.ProjectNotLoaded, ["Legacy.Tests"], AgentsRead.Note, "Expected: legacy tests.");

		var restarted = Health(home, clock);
		var accepted = restarted.Describe(Shop, [Legacy]).ShouldHaveSingleItem().Accepted;

		accepted.ShouldNotBeNull();
		accepted.AgentsRead.ShouldBe(AgentsRead.Note);
		accepted.Note.ShouldBe("Expected: legacy tests.");
		accepted.AcceptedUtc.ShouldBe(clock.Now);

		var heard = restarted.HeardFor(Shop).ShouldHaveSingleItem();
		heard.Read.ShouldBe(AgentsRead.Note);
		heard.Covering.ShouldBe("Legacy.Tests");
	}

	/// <summary>
	/// Accepted per kind and per project: a reason that also names a project the person never accepted it for,
	/// or one of another kind about the same project, is not covered.
	/// </summary>
	[Test]
	public void An_acceptance_covers_its_kind_and_projects_and_nothing_else()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		health.Accept(Shop, ReasonKinds.MsBuildLoadFailures, ["Legacy.Tests"], AgentsRead.Nothing, null);

		var described = health.Describe(
			Shop,
			[
				Reason(ReasonKinds.MsBuildLoadFailures, "Legacy.Tests"),
				Reason(ReasonKinds.MsBuildLoadFailures, "Legacy.Tests", "Ink.Tests"),
				Reason(ReasonKinds.ProjectNotLoaded, "Legacy.Tests"),
			]);

		described.Select(reason => reason.Accepted is not null).ShouldBe([true, false, false]);

		// And per solution: the same reason in another checkout is not covered.
		var elsewhere = Path.Combine(Path.GetTempPath(), "rose-health", "worktree", "Shop.slnx");
		health.Describe(elsewhere, [Reason(ReasonKinds.MsBuildLoadFailures, "Legacy.Tests")]).ShouldHaveSingleItem().Accepted.ShouldBeNull();
	}

	[Test]
	public void Stopping_accepting_is_remembered_too()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		health.Accept(Shop, ReasonKinds.ProjectNotLoaded, ["Legacy.Tests"], AgentsRead.Nothing, null);
		health.StopAccepting(Shop, ReasonKinds.ProjectNotLoaded, ["legacy.tests"]).Accepted.ShouldBeEmpty();

		Health(home).Describe(Shop, [Legacy]).ShouldHaveSingleItem().Accepted.ShouldBeNull();
	}

	[Test]
	public void An_acceptance_asking_for_a_note_needs_one()
	{
		using var home = new TemporaryLocalAppData();

		Should.Throw<ArgumentException>(() => Health(home).Accept(Shop, ReasonKinds.RestoreFailed, [], AgentsRead.Note, " "));
	}

	[Test]
	public void A_solution_is_named_by_its_absolute_path()
	{
		using var home = new TemporaryLocalAppData();

		Should.Throw<ArgumentException>(() => Health(home).Accepted("Shop.slnx"));
	}

	/// <summary>One session's acknowledgement quiets its own reads and nobody else's.</summary>
	[Test]
	public void An_acknowledgement_is_the_sessions_own()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		using (CallSession.Use("session-one"))
		{
			health.Acknowledge(Shop, Legacy);

			health.HeardFor(Shop).ShouldHaveSingleItem().Read.ShouldBe(AgentsRead.Nothing);
			health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().AcknowledgedUtc.ShouldNotBeNull();
		}

		using (CallSession.Use("session-two"))
		{
			health.HeardFor(Shop).ShouldBeEmpty();
			health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().AcknowledgedUtc.ShouldBeNull();
		}

		// Nor does it outlive the broker: it is a session's, and a session ends with the process at the latest.
		using (CallSession.Use("session-one"))
		{
			Health(home).HeardFor(Shop).ShouldBeEmpty();
		}
	}

	/// <summary>An acknowledgement wins over an acceptance of the same kind and project: the session asked to hear nothing.</summary>
	[Test]
	public void An_acknowledgement_quiets_what_an_acceptance_would_have_said()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);
		health.Accept(Shop, ReasonKinds.ProjectNotLoaded, ["Legacy.Tests"], AgentsRead.RoseLine, null);

		using (CallSession.Use("session-one"))
		{
			health.Acknowledge(Shop, Legacy);
			health.HeardFor(Shop).ShouldHaveSingleItem().Read.ShouldBe(AgentsRead.Nothing);
		}
	}

	/// <summary>
	/// On a broker serving several clients, a call carrying no session is any of them, so acknowledging from one
	/// would quiet the reason for all: refused, saying what to do instead. Over stdio there is one client, and it
	/// is that one.
	/// </summary>
	[Test]
	public void A_call_with_no_session_acknowledges_only_where_the_broker_has_one_client()
	{
		using var home = new TemporaryLocalAppData();

		var refused = Should.Throw<InvalidOperationException>(() => Health(home).Acknowledge(Shop, Legacy));
		refused.Message.ShouldContain("propose", Case.Sensitive);

		var stdio = Health(home, oneClient: true);
		stdio.Acknowledge(Shop, Legacy);
		stdio.HeardFor(Shop).ShouldHaveSingleItem().Read.ShouldBe(AgentsRead.Nothing);
	}

	/// <summary>
	/// A session that has made no call for the transport's idle limit has ended, and what it acknowledged and
	/// proposed goes with it, so a long-lived broker does not collect every session that ever spoke to it.
	/// </summary>
	[Test]
	public void A_session_gone_quiet_takes_its_acknowledgements_and_proposals_with_it()
	{
		using var home = new TemporaryLocalAppData();
		var clock = new SteerableClock(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
		var health = Health(home, clock);

		using (CallSession.Use("session-one"))
		{
			health.Acknowledge(Shop, Legacy);
			health.Propose(Shop, Reason(ReasonKinds.RestoreFailed), null);
		}

		health.Proposals().ShouldHaveSingleItem();
		clock.Advance(WorkspaceHealth.SessionIdleLimit + TimeSpan.FromMinutes(1));

		health.Proposals().ShouldBeEmpty();

		using (CallSession.Use("session-one"))
		{
			health.HeardFor(Shop).ShouldBeEmpty();
		}
	}

	[Test]
	public void A_session_forgotten_is_gone_at_once()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		using (CallSession.Use("session-one"))
		{
			health.Acknowledge(Shop, Legacy);
			health.Propose(Shop, Legacy, null);
		}

		health.ForgetSession("session-one");

		health.Proposals().ShouldBeEmpty();

		using (CallSession.Use("session-one"))
		{
			health.HeardFor(Shop).ShouldBeEmpty();
		}
	}

	/// <summary>A proposal waits for the person, and accepts nothing until they confirm it; confirming can change what agents read.</summary>
	[Test]
	public void A_proposal_waits_for_the_person_and_their_confirmation_accepts_it()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		ReasonProposal proposal;
		using (CallSession.Use("session-one"))
		{
			proposal = health.Propose(Shop, Legacy, "Legacy tests; ignore unless working in them.");
			health.Propose(Shop, Legacy, "Legacy tests nobody works in.").Id.ShouldBe(proposal.Id, "proposing twice leaves one thing to decide");
		}

		proposal.AgentsRead.ShouldBe(AgentsRead.Note);
		health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().Accepted.ShouldBeNull();

		var waiting = health.Proposals().ShouldHaveSingleItem();
		waiting.Note.ShouldBe("Legacy tests nobody works in.");
		waiting.Projects.ShouldBe(["Legacy.Tests"]);

		var accepted = health.Confirm(proposal.Id, AgentsRead.Nothing, null).Accepted.ShouldHaveSingleItem();
		accepted.AgentsRead.ShouldBe(AgentsRead.Nothing);

		health.Proposals().ShouldBeEmpty();
		health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().Accepted.ShouldNotBeNull();
	}

	[Test]
	public void A_rejected_proposal_accepts_nothing()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		var proposal = health.Propose(Shop, Legacy, null);
		health.Reject(proposal.Id).Id.ShouldBe(proposal.Id);

		health.Proposals().ShouldBeEmpty();
		health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().Accepted.ShouldBeNull();
		Should.Throw<KeyNotFoundException>(() => health.Confirm(proposal.Id, null, null));
	}

	/// <summary>
	/// A stdio server running on its own serves nothing a person could confirm a proposal on, so a proposal there
	/// would wait for ever: refused, saying why and what to do instead.
	/// </summary>
	[Test]
	public void A_host_with_no_operator_api_refuses_a_proposal_nobody_could_confirm()
	{
		using var home = new TemporaryLocalAppData();
		var stdio = Health(home, oneClient: true, operatorApi: false);

		var refused = Should.Throw<InvalidOperationException>(() => stdio.Propose(Shop, Legacy, null));

		refused.Message.ShouldContain("no operator API", Case.Sensitive);
		refused.Message.ShouldContain("tray", Case.Sensitive);
		stdio.Proposals().ShouldBeEmpty();
	}

	/// <summary>A confirmation that fails -- here for want of the note it asks agents to read -- leaves the proposal to be decided again.</summary>
	[Test]
	public void A_confirmation_that_fails_leaves_the_proposal_waiting()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);
		var proposal = health.Propose(Shop, Legacy, null);

		Should.Throw<ArgumentException>(() => health.Confirm(proposal.Id, AgentsRead.Note, null));

		health.Proposals().ShouldHaveSingleItem().Id.ShouldBe(proposal.Id);
		health.Describe(Shop, [Legacy]).ShouldHaveSingleItem().Accepted.ShouldBeNull();
	}

	/// <summary>The one client of a stdio broker is its session for the life of the process, however long it is quiet.</summary>
	[Test]
	public void A_stdio_brokers_one_client_never_goes_quiet()
	{
		using var home = new TemporaryLocalAppData();
		var clock = new SteerableClock(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
		var stdio = Health(home, clock, oneClient: true, operatorApi: false);

		stdio.Acknowledge(Shop, Legacy);
		clock.Advance(WorkspaceHealth.SessionIdleLimit + TimeSpan.FromHours(6));

		stdio.HeardFor(Shop).ShouldHaveSingleItem().Read.ShouldBe(AgentsRead.Nothing);
	}

	/// <summary>
	/// A reason naming no project is accepted for what it is about: an assembly accepted as failing does not cover
	/// a different one that starts failing later, which is a cause nobody has seen.
	/// </summary>
	[Test]
	public void A_reason_about_no_project_is_accepted_for_its_subjects()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);

		health.Accept(Shop, ReasonKinds.AnalyzerLoadFailed, [], AgentsRead.Nothing, null, ["Old.Generator.dll"]);

		var described = health.Describe(
			Shop,
			[
				About(ReasonKinds.AnalyzerLoadFailed, "Old.Generator.dll"),
				About(ReasonKinds.AnalyzerLoadFailed, "Old.Generator.dll", "New.Generator.dll"),
			]);

		described.Select(reason => reason.Accepted is not null).ShouldBe([true, false]);

		static HealthReason About(string kind, params string[] subjects) =>
			Reason(kind) with { Subjects = subjects };
	}

	/// <summary>
	/// The id a real relay mints, read as the broker's call filter reads it: a session of that shape is told apart
	/// from another of the same shape, and a call with none on a broker serving several is refused.
	/// </summary>
	[Test]
	public void A_relayed_session_is_its_own_by_the_id_its_relay_sends()
	{
		using var home = new TemporaryLocalAppData();
		var health = Health(home);
		var mine = CallSession.Of(null, Marked(CallSession.Mint()));
		var theirs = CallSession.Of(null, Marked(CallSession.Mint()));

		mine.ShouldNotBeNull();
		theirs.ShouldNotBeNull();

		using (CallSession.Use(mine))
		{
			health.Acknowledge(Shop, Legacy);
			health.HeardFor(Shop).ShouldHaveSingleItem();
		}

		using (CallSession.Use(theirs))
		{
			health.HeardFor(Shop).ShouldBeEmpty();
		}

		using (CallSession.Use(CallSession.Of(null, null)))
		{
			Should.Throw<InvalidOperationException>(() => health.Acknowledge(Shop, Legacy));
		}

		static System.Text.Json.Nodes.JsonObject Marked(string id) => new() { [CallSession.MetaKey] = id };
	}

	/// <summary>A broker's health, serving the operator API unless a test says it is a stdio server on its own.</summary>
	private static WorkspaceHealth Health(
		TemporaryLocalAppData home,
		TimeProvider? clock = null,
		bool oneClient = false,
		bool operatorApi = true)
	{
		var health = new WorkspaceHealth(
			Options.Create(new BrokerOptions
			{
				LocalAppData = home.Path,
				TimeProvider = clock ?? TimeProvider.System,
				ServesOneClient = oneClient,
			}),
			NullLogger<WorkspaceHealth>.Instance);

		if (operatorApi) health.OfferToThePerson();

		return health;
	}

	private static HealthReason Reason(string kind, params string[] projects) => new()
	{
		Id = projects.Length == 0 ? kind : $"{kind}:{projects[0]}",
		Kind = kind,
		Effect = HealthEffect.MayAffectSomeProjects,
		Projects = projects,
		Line = $"{kind} for {string.Join(", ", projects)}.",
	};

	/// <summary>A clock a test moves by hand, so a month passes in a line.</summary>
	private sealed class SteerableClock(DateTime start) : TimeProvider
	{
		public DateTime Now { get; private set; } = start;

		public void Advance(TimeSpan by) => Now += by;

		public override DateTimeOffset GetUtcNow() => new(Now);
	}
}
