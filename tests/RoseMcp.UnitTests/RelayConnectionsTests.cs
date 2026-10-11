using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The broker's side of the relay protocol: which relays are connected, which is what a deploy
/// counts to say how many sessions found their way back, and the shape a relay's id is held to.
/// </summary>
public sealed class RelayConnectionsTests
{
	/// <summary>
	/// A relay is counted while its watch is open and once however many it holds, since a relay
	/// re-opening its watch while the old one is torn down is still one session.
	/// </summary>
	[Test]
	public void Counts_each_relay_once_while_it_watches()
	{
		var relays = new RelayConnections();
		var one = RelayProtocol.NewRelayId();
		var other = RelayProtocol.NewRelayId();

		var first = relays.Watch(one);
		var again = relays.Watch(one);
		var second = relays.Watch(other);

		relays.Count.ShouldBe(2);

		first.Dispose();
		relays.Count.ShouldBe(2, "the relay still holds a watch");

		again.Dispose();
		again.Dispose();
		relays.Count.ShouldBe(1, "a watch released twice counts once");

		second.Dispose();
		relays.Count.ShouldBe(0);
	}

	[Test]
	public void Refuses_an_id_not_in_the_shape_a_relay_mints()
	{
		var relays = new RelayConnections();

		Should.Throw<ArgumentException>(() => relays.Watch("not-a-relay")).ShouldBeOfType<ArgumentException>()
			.Message.ShouldContain(RelayProtocol.RelayHeader, Case.Sensitive);

		RelayProtocol.IsRelayId(RelayProtocol.NewRelayId()).ShouldBeTrue();
		RelayProtocol.IsRelayId(new string('A', 32)).ShouldBeFalse("ids are lowercase");
		RelayProtocol.IsRelayId(new string('a', 33)).ShouldBeFalse();
		RelayProtocol.IsRelayId(null).ShouldBeFalse();
	}

	[Test]
	public void Says_which_broker_it_is_and_the_protocol_it_speaks()
	{
		var relays = new RelayConnections();
		using var watching = relays.Watch(RelayProtocol.NewRelayId());

		var build = new BuildIdentity { Version = "1.3.0", Commit = new string('e', 40) };
		var hello = relays.Hello(build);

		hello.Instance.ShouldBe(relays.Instance);
		hello.Instance.ShouldNotBe(new RelayConnections().Instance, "every broker start is a new instance");
		hello.Build.ShouldBe(build);
		hello.RelayProtocol.ShouldBe(RelayProtocol.Version);
		hello.Relays.ShouldBe(1);
	}

	/// <summary>
	/// The reply names the processes of the relays watching, so a deploy counts only the relays of the
	/// install it replaced: every relay on the port is in the count, one from a build output included.
	/// A relay that sent no process is counted and named as none, and one that leaves takes its id with it.
	/// </summary>
	[Test]
	public void Names_the_processes_of_the_relays_watching()
	{
		var relays = new RelayConnections();
		var build = new BuildIdentity { Version = "1.3.0", Commit = new string('e', 40) };
		var one = RelayProtocol.NewRelayId();

		var first = relays.Watch(one, 4200);
		var again = relays.Watch(one, processId: null);
		var other = relays.Watch(RelayProtocol.NewRelayId(), 310);
		using var silent = relays.Watch(RelayProtocol.NewRelayId());

		var hello = relays.Hello(build);
		hello.Relays.ShouldBe(3);
		hello.RelayProcesses.ShouldBe([310, 4200], "ascending, a re-opened watch keeping the process the relay said");

		first.Dispose();
		relays.Hello(build).RelayProcesses.ShouldBe([310, 4200], "the relay still holds a watch");

		again.Dispose();
		other.Dispose();
		relays.Hello(build).RelayProcesses.ShouldBeEmpty();
		relays.Hello(build).Relays.ShouldBe(1);
	}

	[Test]
	[Arguments("4200", 4200)]
	[Arguments("", null)]
	[Arguments(null, null)]
	[Arguments("0", null)]
	[Arguments("-12", null)]
	[Arguments(" 12", null)]
	[Arguments("12a", null)]
	[Arguments("99999999999", null)]
	public void Reads_a_relay_process_only_as_a_positive_decimal(string? header, int? expected) =>
		RelayConnections.ParseProcessId(header).ShouldBe(expected);

	/// <summary>
	/// A relay with no tray starts the install's broker with <c>--no-relay</c>, so the child hosts
	/// the broker rather than looking for a tray to relay to, and with the session's own settings.
	/// </summary>
	[Test]
	public void The_child_broker_is_started_so_it_cannot_relay_in_turn()
	{
		var options = ServerOptions.Parse(["--port", "5099", "--worker", @"C:\Rose\RoseMcp.Worker.exe", "--no-restore"]);

		options.NoRelay.ShouldBeFalse();
		options.ChildBrokerArguments().ShouldBe(
			["--no-relay", "--host", "127.0.0.1", "--port", "5099", "--worker", @"C:\Rose\RoseMcp.Worker.exe", "--no-restore"]);

		ServerOptions.Parse(options.ChildBrokerArguments().ToArray()).NoRelay.ShouldBeTrue();
	}
}
