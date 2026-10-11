using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The broker's table of clients: who each is, when it connected and last called, whether it holds the
/// broker's tools, and that a row goes when its client does -- the broker outlives every client, so a row
/// that never went would be a leak with a person's eyes on it.
/// </summary>
public sealed class ConnectedClientsTests
{
	private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

	private static readonly string Folder = Path.Combine(Path.GetTempPath(), "rose-clients", "Shop");

	private static readonly string Hash = new('a', 64);

	[Test]
	public void An_introduction_is_kept_with_when_the_client_connected_and_its_relay()
	{
		var (clients, relays, clock) = Table();
		var relay = RelayProtocol.NewRelayId();
		using var watching = relays.Watch(relay, processId: 4242);

		clock.Advance(TimeSpan.FromMinutes(1));
		clients.Introduce(relay, Facts(CallSession.Mint()) with { ToolsCurrent = true, HeldTools = Hash });

		var row = clients.Describe().ShouldHaveSingleItem();
		row.Named.ShouldBeTrue();
		row.Id.ShouldStartWith("client-", Case.Sensitive);
		row.ConnectedUtc.ShouldBe(Start + TimeSpan.FromMinutes(1));
		row.LastCallUtc.ShouldBeNull("it has said who it is and called nothing");
		row.Watching.ShouldBe(true);
		row.RelayProcessId.ShouldBe(4242);
		row.WorkingDirectory.ShouldBe(Folder);
		row.Roots.ShouldBe([Folder]);
		row.Branch.ShouldBe("main");
		row.ClientProcessId.ShouldBe(1717);
		row.ClientProcessName.ShouldBe("claude");
		row.ClientName.ShouldBe("claude-code");
		row.ClientVersion.ShouldBe("2.1.300");
		row.Label.ShouldBe("card 5.4");
		row.ToolsCurrent.ShouldBe(true);
		row.HeldTools.ShouldBe(Hash);
	}

	/// <summary>
	/// A call, a listing and an introduction each wake whoever waits on the table, which answers a window's long poll
	/// at once; a relay's own read changes nothing a row shows and wakes nobody.
	/// </summary>
	[Test]
	public void Whatever_changes_a_row_wakes_the_tables_waiters()
	{
		var (clients, _, _) = Table();
		var session = CallSession.Relayed(CallSession.Mint());

		var beforeRead = clients.Changed.Next;
		clients.ReadByRelay(session, transportSession: null);
		beforeRead.IsCompleted.ShouldBeFalse();

		clients.Called(session, transportSession: null, clientName: null, clientVersion: null);
		beforeRead.IsCompleted.ShouldBeTrue();

		var beforeListing = clients.Changed.Next;
		clients.Listed(relayed: null, transportSession: null, "probe", "1.0");
		beforeListing.IsCompleted.ShouldBeTrue();

		var beforeIntroduction = clients.Changed.Next;
		clients.Introduce(RelayProtocol.NewRelayId(), Facts(CallSession.Mint()));
		beforeIntroduction.IsCompleted.ShouldBeTrue();
	}

	/// <summary>Subagents share their session's one connection and so its one relay: every call they make is the one client's.</summary>
	[Test]
	public void One_relay_making_many_calls_is_one_client()
	{
		var (clients, relays, clock) = Table();
		var relay = RelayProtocol.NewRelayId();
		var session = CallSession.Mint();
		using var watching = relays.Watch(relay);

		clients.Introduce(relay, Facts(session));
		for (var call = 0; call < 5; call++)
		{
			clock.Advance(TimeSpan.FromSeconds(10));
			clients.Called(CallSession.Relayed(session), transportSession: null, "rose-relay", "1.0");
		}

		var row = clients.Describe().ShouldHaveSingleItem();
		row.Calls.ShouldBe(5);
		row.LastCallUtc.ShouldBe(Start + TimeSpan.FromSeconds(50));
		row.ClientName.ShouldBe("claude-code", "a relayed call's clientInfo is the relay's own, and the relay said whom it serves");
	}

	/// <summary>Two sessions in one folder are two relay processes, each with its own session name, and so two clients.</summary>
	[Test]
	public void Two_relays_in_one_folder_are_two_clients()
	{
		var (clients, relays, _) = Table();
		var first = RelayProtocol.NewRelayId();
		var second = RelayProtocol.NewRelayId();
		using var one = relays.Watch(first, 100);
		using var other = relays.Watch(second, 200);

		clients.Introduce(first, Facts(CallSession.Mint()));
		clients.Introduce(second, Facts(CallSession.Mint()));

		var rows = clients.Describe();
		rows.Count.ShouldBe(2);
		rows.Select(row => row.Id).Distinct().Count().ShouldBe(2);
		rows.Select(row => row.WorkingDirectory).ShouldAllBe(directory => directory == Folder);
		rows.Select(row => row.RelayProcessId).Order().ShouldBe([100, 200]);
	}

	/// <summary>A call a relay makes before it has said whom it serves is still the session's, and the introduction joins it.</summary>
	[Test]
	public void A_call_before_the_introduction_is_joined_to_it()
	{
		var (clients, relays, _) = Table();
		var relay = RelayProtocol.NewRelayId();
		var session = CallSession.Mint();
		using var watching = relays.Watch(relay);

		clients.Listed(CallSession.Relayed(session), null, null, null);
		clients.Describe().ShouldBeEmpty("a relay lists the broker for itself before its client asks anything");

		clients.Called(CallSession.Relayed(session), null, null, null);
		clients.Introduce(relay, Facts(session));

		var row = clients.Describe().ShouldHaveSingleItem();
		row.Calls.ShouldBe(1);
		row.Label.ShouldBe("card 5.4");
		row.Watching.ShouldBe(true);
	}

	[Test]
	public void A_session_another_relay_named_is_refused()
	{
		var (clients, _, _) = Table();
		var session = CallSession.Mint();

		clients.Introduce(RelayProtocol.NewRelayId(), Facts(session));

		Should.Throw<InvalidOperationException>(() => clients.Introduce(RelayProtocol.NewRelayId(), Facts(session) with { Label = "imposter" }));
		clients.Describe().ShouldHaveSingleItem().Label.ShouldBe("card 5.4");

		Should.Throw<ArgumentException>(() => clients.Introduce("not-a-relay", Facts(CallSession.Mint())));
		Should.Throw<ArgumentException>(() => clients.Introduce(RelayProtocol.NewRelayId(), Facts("NOT A SESSION")));
	}

	/// <summary>
	/// A relayed client stays while its relay watches however quiet it is, and goes a grace after the relay stops --
	/// counted from the stop, not from its last word, or a quiet client whose relay is only re-opening its watch
	/// would lose its row, and everything its relay said of it, in the moment between.
	/// </summary>
	[Test]
	public void A_relayed_client_goes_a_grace_after_its_relay_stops_watching()
	{
		var (clients, relays, clock) = Table();
		var relay = RelayProtocol.NewRelayId();
		var watching = relays.Watch(relay);

		clients.Introduce(relay, Facts(CallSession.Mint()));

		clock.Advance(ConnectedClients.IdleLimit + TimeSpan.FromHours(1));
		clients.Describe().ShouldHaveSingleItem("its relay is watching, so it is connected however long it is quiet");

		watching.Dispose();
		var row = clients.Describe().ShouldHaveSingleItem("its relay let go a moment ago, which may be a re-opening");
		row.Watching.ShouldBe(false);
		row.Label.ShouldBe("card 5.4");

		clock.Advance(ConnectedClients.GoneGrace + TimeSpan.FromSeconds(1));
		clients.Describe().ShouldBeEmpty("its relay has been gone past the grace");
	}

	/// <summary>A relay that re-opens its watch within the grace keeps its client's row as it was.</summary>
	[Test]
	public void A_quiet_client_keeps_its_row_while_its_relay_reopens_the_watch()
	{
		var (clients, relays, clock) = Table();
		var relay = RelayProtocol.NewRelayId();

		var first = relays.Watch(relay);
		clients.Introduce(relay, Facts(CallSession.Mint()));
		clock.Advance(TimeSpan.FromHours(1));

		first.Dispose();
		clock.Advance(TimeSpan.FromSeconds(2));
		using var again = relays.Watch(relay);

		clock.Advance(ConnectedClients.GoneGrace + TimeSpan.FromMinutes(1));
		var row = clients.Describe().ShouldHaveSingleItem();
		row.Watching.ShouldBe(true);
		row.WorkingDirectory.ShouldBe(Folder);
	}

	/// <summary>A read the relay makes for itself says its client is there, and is not one of the client's calls.</summary>
	[Test]
	public void A_relays_own_read_is_not_counted_as_a_call()
	{
		var (clients, relays, _) = Table();
		var relay = RelayProtocol.NewRelayId();
		var session = CallSession.Mint();
		using var watching = relays.Watch(relay);

		clients.ReadByRelay(CallSession.Relayed(session), null);
		clients.Describe().ShouldBeEmpty("a relay's own read makes no client");

		clients.Introduce(relay, Facts(session));
		clients.Called(CallSession.Relayed(session), null, null, null);
		clients.ReadByRelay(CallSession.Relayed(session), null);

		clients.Describe().ShouldHaveSingleItem().Calls.ShouldBe(1);

		CallSession.IsRelayRead(new() { [CallSession.RelayReadKey] = true }).ShouldBeTrue();
		CallSession.IsRelayRead(new() { [CallSession.RelayReadKey] = "yes" }).ShouldBeFalse();
		CallSession.IsRelayRead(null).ShouldBeFalse();
	}

	[Test]
	public void A_relay_reopening_its_watch_keeps_its_row_through_the_grace()
	{
		var (clients, relays, clock) = Table();
		var relay = RelayProtocol.NewRelayId();

		using (relays.Watch(relay))
		{
			clients.Introduce(relay, Facts(CallSession.Mint()));
		}

		clock.Advance(ConnectedClients.GoneGrace - TimeSpan.FromSeconds(1));
		var row = clients.Describe().ShouldHaveSingleItem();
		row.Watching.ShouldBe(false);

		clock.Advance(TimeSpan.FromSeconds(2));
		clients.Describe().ShouldBeEmpty();
	}

	/// <summary>
	/// A client that reached the broker over http with no relay is unnamed. Without a transport session every such
	/// client is one row, since nothing says which of them called; with one, each session is its own.
	/// </summary>
	[Test]
	public void Clients_without_a_relay_are_unnamed_and_go_when_idle()
	{
		var (clients, _, clock) = Table();

		clients.Called(null, null, "inspector-script", "0.1");
		clients.Called(null, null, "inspector-script", "0.1");
		clients.Called(null, "transport-1", "old-client", "1.0");

		var rows = clients.Describe();
		rows.Count.ShouldBe(2);
		rows.ShouldAllBe(row => !row.Named && row.Watching == null);

		var shared = rows.Single(row => row.ClientName == "inspector-script");
		shared.Calls.ShouldBe(2);
		shared.ToolsCurrent.ShouldBe(false, "it calls with tools it never listed from this broker");

		clients.Listed(null, null, "inspector-script", "0.1");
		clients.Describe().Single(row => row.ClientName == "inspector-script").ToolsCurrent.ShouldBe(true);

		clock.Advance(ConnectedClients.IdleLimit + TimeSpan.FromMinutes(1));
		clients.Describe().ShouldBeEmpty("nothing tells the broker an unnamed client has gone, so its silence does");
	}

	/// <summary>Whether a client holds the broker's tools is what its relay last said, and a later introduction replaces it.</summary>
	[Test]
	public void The_tool_match_is_what_the_relay_last_said()
	{
		var (clients, relays, _) = Table();
		var relay = RelayProtocol.NewRelayId();
		var session = CallSession.Mint();
		using var watching = relays.Watch(relay);

		clients.Introduce(relay, Facts(session) with { HeldTools = Hash, ToolsCurrent = false });
		clients.Describe().ShouldHaveSingleItem().ToolsCurrent.ShouldBe(false);

		clients.Introduce(relay, Facts(session) with { HeldTools = new string('b', 64), ToolsCurrent = true });
		var row = clients.Describe().ShouldHaveSingleItem();
		row.ToolsCurrent.ShouldBe(true);
		row.HeldTools.ShouldBe(new string('b', 64));
	}

	/// <summary>What a relay sends is shown to a person and logged, so it is kept only in a shape that cannot mislead either.</summary>
	[Test]
	public void What_a_relay_says_is_cleaned_before_it_is_kept()
	{
		var cleaned = ConnectedClients.Sanitised(Facts(CallSession.Mint()) with
		{
			Label = "deploy\u001b[31m now\r\nforged line\u202e",
			ClientName = new string('x', 1000),
			Roots = [.. Enumerable.Range(0, 40).Select(index => $"D:\\root{index}"), "   "],
			ClientProcessId = -3,
			HeldTools = "not a hash",
		});

		cleaned.Label.ShouldBe("deploy[31m nowforged line");
		cleaned.ClientName!.Length.ShouldBe(128);
		cleaned.Roots.Count.ShouldBe(16);
		cleaned.ClientProcessId.ShouldBeNull();
		cleaned.HeldTools.ShouldBeNull();
	}

	/// <summary>The table is bounded: a newcomer past it takes the place of the quietest client whose relay has gone.</summary>
	[Test]
	public void A_full_table_makes_room_by_dropping_the_quietest_client_not_watching()
	{
		var (clients, _, clock) = Table();

		for (var index = 0; index < ConnectedClients.Capacity; index++)
		{
			clients.Called(null, $"transport-{index}", null, null);
			clock.Advance(TimeSpan.FromSeconds(1));
		}

		clients.Called(null, "transport-newest", null, null);

		var rows = clients.Describe();
		rows.Count.ShouldBe(ConnectedClients.Capacity);
		rows[0].ConnectedUtc.ShouldBe(Start + TimeSpan.FromSeconds(1), "the first and quietest went to make room");
	}

	private static RelayClient Facts(string session) => new()
	{
		Session = session,
		WorkingDirectory = Folder,
		Roots = [Folder],
		Branch = "main",
		ClientProcessId = 1717,
		ClientProcessName = "claude",
		ClientName = "claude-code",
		ClientVersion = "2.1.300",
		Label = "card 5.4",
	};

	private static (ConnectedClients Clients, RelayConnections Relays, SteerableClock Clock) Table()
	{
		var clock = new SteerableClock(Start);
		var relays = new RelayConnections(clock);
		var clients = new ConnectedClients(relays, Options.Create(new BrokerOptions { TimeProvider = clock }));

		return (clients, relays, clock);
	}

	private sealed class SteerableClock(DateTimeOffset start) : TimeProvider
	{
		private DateTimeOffset _now = start;

		public void Advance(TimeSpan by) => _now += by;

		public override DateTimeOffset GetUtcNow() => _now;
	}
}
