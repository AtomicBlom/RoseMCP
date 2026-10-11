using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Who is in a debug session, as a window is told it: the agent session that started it, the one a person shared
/// theirs with, and who set the breakpoint the target is stopped at -- each named by the row the client table lists
/// it under now, and none of them guessed.
/// </summary>
public sealed class SessionPartiesTests
{
	private const string StarterKey = "relay:relayed-aaaaaaaaaaaaaaaa";
	private const string SetterKey = "relay:relayed-bbbbbbbbbbbbbbbb";

	private static readonly Dictionary<string, ClientSummary> Present = new(StringComparer.Ordinal)
	{
		[StarterKey] = Client("client-1", @"D:\Drawboard\PDF"),
		[SetterKey] = Client("client-2", @"D:\Other"),
	};

	/// <summary>An agent's session names the agent session that started it, by its row and its folder.</summary>
	[Test]
	public void An_agents_session_names_the_session_that_started_it()
	{
		var named = OperatorViews.Named(Summary(), LiveAppOwners.Agent("mcp-one", StarterKey), setter: null, Present);

		var starter = named.StartedByAgent.ShouldNotBeNull();
		starter.ClientId.ShouldBe("client-1");
		starter.Name.ShouldBe(@"D:\Drawboard\PDF");
		starter.Connected.ShouldBe(true);
	}

	/// <summary>
	/// A starter the table no longer lists is not named: a row that has gone is one a window can neither name nor send
	/// a note to, and a person's session has no agent starter at all.
	/// </summary>
	[Test]
	public void A_starter_the_table_no_longer_lists_and_a_persons_session_name_none()
	{
		OperatorViews.Named(Summary(), LiveAppOwners.Agent("mcp-one", "relay:relayed-gone0000000000000"), null, Present).StartedByAgent.ShouldBeNull();
		OperatorViews.Named(Summary(), LiveAppOwners.Agent(null), null, Present).StartedByAgent.ShouldBeNull();
		OperatorViews.Named(Summary(), LiveAppOwners.Person, null, Present).StartedByAgent.ShouldBeNull();
	}

	/// <summary>A stop at a breakpoint says who set it: the person, or the agent session by its row.</summary>
	[Test]
	public void A_stop_at_a_breakpoint_says_who_set_it()
	{
		var atBreakpoint = Summary(Stop("bp-1"));

		var byPerson = OperatorViews.Named(atBreakpoint, LiveAppOwners.Person, LiveAppActor.Person, Present).StopSetBy.ShouldNotBeNull();
		byPerson.Person.ShouldBeTrue();
		byPerson.Agent.ShouldBeNull();

		var byAgent = OperatorViews.Named(atBreakpoint, LiveAppOwners.Person, new LiveAppActor(SetterKey), Present).StopSetBy.ShouldNotBeNull();
		byAgent.Person.ShouldBeFalse();
		byAgent.Agent.ShouldNotBeNull().ClientId.ShouldBe("client-2");

		var byGone = OperatorViews.Named(atBreakpoint, LiveAppOwners.Person, new LiveAppActor("relay:relayed-gone0000000000000"), Present).StopSetBy.ShouldNotBeNull();
		byGone.Person.ShouldBeFalse("an agent set it, whether or not its session is still listed");
		byGone.Agent.ShouldBeNull();
	}

	/// <summary>A stop no breakpoint made, and a target running, say nothing about who set anything.</summary>
	[Test]
	public void A_step_or_a_running_target_names_no_setter()
	{
		OperatorViews.Named(Summary(Stop(breakpointId: null)), LiveAppOwners.Person, LiveAppActor.Person, Present).StopSetBy.ShouldBeNull();
		OperatorViews.Named(Summary(), LiveAppOwners.Person, LiveAppActor.Person, Present).StopSetBy.ShouldBeNull();
	}

	/// <summary>
	/// The key that names the starter rides along when the person-only parts change, though a person's session never
	/// has one: sharing replaces the shared client and nothing else.
	/// </summary>
	[Test]
	public void An_agent_started_session_keeps_its_starters_key_as_its_owners_change()
	{
		using (CallSession.Use(id: null, client: "relayed-aaaaaaaaaaaaaaaa", relayed: "relayed-aaaaaaaaaaaaaaaa"))
		{
			var owners = LiveAppOwners.OfCaller();

			owners.StarterKey.ShouldBe(CallSession.ClientKey);
			owners.Unshared().StarterKey.ShouldBe(owners.StarterKey);
		}

		LiveAppOwners.Person.StarterKey.ShouldBeNull();
	}

	/// <summary>
	/// The broker records who set each breakpoint as it is set, by the id the host gave it, which is the only place it
	/// can be known: the host is never told who asked.
	/// </summary>
	[Test]
	public async Task Each_breakpoint_set_is_recorded_against_who_set_it()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var hosts = new FakeLiveAppHost();
		await using var sessions = new LiveAppSessionManager(
			Options.Create(new BrokerOptions { LiveAppHost = hosts.For, AppUserModelIdOf = _ => null }),
			NullLoggerFactory.Instance,
			NullLogger<LiveAppSessionManager>.Instance);

		var session = await sessions.StartAsync(
			new LiveAppTarget { Kind = LiveAppTargetKind.AttachProcess, ProcessId = 4545, Description = "probe" },
			LiveAppOwners.Person,
			cancellationToken);

		var mine = await session.SetBreakpointAsync("Probe.Program.Beat", autoContinueSeconds: null, condition: null, cancellationToken);

		LiveAppActor agent;
		LiveBreakpointBatch theirs;
		using (CallSession.Use(id: null, client: "relayed-bbbbbbbbbbbbbbbb", relayed: "relayed-bbbbbbbbbbbbbbbb"))
		{
			agent = LiveAppActor.OfCaller();
			theirs = await session.SetBreakpointsAsync([new SetBreakpointRequest { Location = "Probe.Program.Beat" }], agent, cancellationToken);
		}

		session.SetterOf(mine.Id).ShouldBe(LiveAppActor.Person);
		session.SetterOf(theirs.Results.ShouldHaveSingleItem().Breakpoint.ShouldNotBeNull().Id).ShouldBe(agent);
		agent.IsPerson.ShouldBeFalse();
		session.SetterOf("bp-99").ShouldBeNull();
		session.SetterOf(null).ShouldBeNull();
	}

	private static ClientSummary Client(string id, string folder) => new()
	{
		Id = id,
		Named = true,
		ConnectedUtc = DateTimeOffset.UnixEpoch,
		WorkingDirectory = folder,
	};

	private static LiveStop Stop(string? breakpointId) => new()
	{
		State = breakpointId is null ? LiveExecutionState.StoppedAtStep : LiveExecutionState.StoppedAtBreakpoint,
		BreakpointId = breakpointId,
		EventSequence = 12,
		StoppedAtUtc = DateTime.UnixEpoch,
		Resume = LiveStopResume.AutoContinue,
		ResumeDeadlineUtc = DateTime.UnixEpoch.AddSeconds(30),
	};

	private static LiveAppSessionSummary Summary(LiveStop? stop = null) => new()
	{
		SessionId = "a1b2c3d4",
		TargetDescription = "probe",
		Architecture = TargetArchitecture.X64,
		State = LiveAppSessionState.Ready,
		StartedUtc = DateTime.UnixEpoch,
		Uptime = TimeSpan.Zero,
		Execution = stop?.State ?? LiveExecutionState.Running,
		Stop = stop,
		XamlStack = XamlStack.Unknown,
		XamlStackReason = "none",
		XamlProvider = LiveXamlProvider.None,
	};
}
