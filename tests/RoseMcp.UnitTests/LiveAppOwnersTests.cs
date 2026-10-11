using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Who a debug session belongs to: the agent session that started it, or the person and the one agent session they
/// shared it with -- and never, for a person's session, an MCP call they did not share it with.
/// </summary>
public sealed class LiveAppOwnersTests
{
	private static readonly LiveAppSharedClient One = new("relay:relayed-one", "client-1", "D:/one");
	private static readonly LiveAppSharedClient Two = new("relay:relayed-two", "client-2", "D:/two");

	/// <summary>An agent's session admits the MCP session that started it and no other.</summary>
	[Test]
	public void An_agents_session_admits_the_session_that_started_it()
	{
		var owners = LiveAppOwners.Agent("mcp-one");

		owners.Admits("mcp-one", "transport:mcp-one").ShouldBeTrue();
		owners.Admits("mcp-two", "transport:mcp-two").ShouldBeFalse();
		owners.Admits(null, "unnamed").ShouldBeFalse();
		owners.StartedBy.ShouldBe(LiveAppStarter.Agent);
	}

	/// <summary>
	/// On a transport with no sessions an agent's session belongs to the null session, which every call without one
	/// matches, as a stdio broker's always has: the process has one client for its whole life.
	/// </summary>
	[Test]
	public void An_agents_session_on_a_transport_without_sessions_admits_every_call_without_one()
	{
		var owners = LiveAppOwners.Agent(null);

		owners.Admits(null, "unnamed").ShouldBeTrue();
		owners.Admits(null, "relay:relayed-one").ShouldBeTrue();
		owners.Admits("mcp-one", "transport:mcp-one").ShouldBeFalse();
	}

	/// <summary>
	/// A session the person started is reached by no MCP call -- not by the null session, which would make it every
	/// stdio and every unnamed client's, and not by any client's key.
	/// </summary>
	[Test]
	public void A_persons_session_admits_no_call_until_it_is_shared()
	{
		var owners = LiveAppOwners.Person;

		owners.Admits(null, "unnamed").ShouldBeFalse();
		owners.Admits(null, null).ShouldBeFalse();
		owners.Admits("mcp-one", "transport:mcp-one").ShouldBeFalse();
		owners.Admits(null, One.Key).ShouldBeFalse();
		owners.StartedBy.ShouldBe(LiveAppStarter.Person);
		owners.DescribeShare().ShouldBeNull();
	}

	/// <summary>Shared, it admits the client it was shared with, by the key the client table files it under, and only that one.</summary>
	[Test]
	public void A_shared_session_admits_the_client_it_was_shared_with()
	{
		var owners = LiveAppOwners.Person.SharedWith(One);

		owners.Admits(null, One.Key).ShouldBeTrue();
		owners.Admits("some-transport", One.Key).ShouldBeTrue();
		owners.Admits(null, Two.Key).ShouldBeFalse();
		owners.Admits(null, "unnamed").ShouldBeFalse();
		owners.DescribeShare().ShouldBe(new LiveAppAgent { ClientId = "client-1", Name = "D:/one" });
	}

	/// <summary>A session is shared with one client at a time: sharing with another takes it from the first.</summary>
	[Test]
	public void Sharing_with_another_client_replaces_the_first()
	{
		var owners = LiveAppOwners.Person.SharedWith(One).SharedWith(Two);

		owners.Admits(null, One.Key).ShouldBeFalse();
		owners.Admits(null, Two.Key).ShouldBeTrue();
	}

	/// <summary>Unsharing leaves the person's session the person's alone, and leaves the original set as it was.</summary>
	[Test]
	public void Unsharing_leaves_the_session_the_persons_alone()
	{
		var shared = LiveAppOwners.Person.SharedWith(One);
		var unshared = shared.Unshared();

		unshared.Admits(null, One.Key).ShouldBeFalse();
		unshared.IsPersons.ShouldBeTrue();
		shared.Admits(null, One.Key).ShouldBeTrue("a set is replaced, never changed under a reader");
	}

	/// <summary>An agent's session is the agent's: the person cannot give it to another agent session.</summary>
	[Test]
	public void An_agents_session_cannot_be_shared()
	{
		var refused = Should.Throw<LiveAppRefusedException>(() => LiveAppOwners.Agent("mcp-one").SharedWith(One));

		refused.Message.ShouldContain("only a session you started can be shared");
	}

	/// <summary>The ambient call is what a manager checks: its transport session and the key its relay's name files it under.</summary>
	[Test]
	public void The_call_in_flight_is_admitted_by_its_relays_name()
	{
		var owners = LiveAppOwners.Person.SharedWith(new LiveAppSharedClient("relay:relayed-abc", "client-1", null));

		using (CallSession.Use(id: null, client: "relayed-abc", relayed: "relayed-abc"))
		{
			CallSession.ClientKey.ShouldBe("relay:relayed-abc");
			owners.AdmitsCaller().ShouldBeTrue();
		}

		using (CallSession.Use(id: null, client: "relayed-xyz", relayed: "relayed-xyz"))
		{
			owners.AdmitsCaller().ShouldBeFalse();
		}

		using (CallSession.Use(id: null))
		{
			CallSession.ClientKey.ShouldBe("unnamed");
			owners.AdmitsCaller().ShouldBeFalse();
		}
	}
}
