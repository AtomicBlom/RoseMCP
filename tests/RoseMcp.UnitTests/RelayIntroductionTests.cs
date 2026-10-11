using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The relay telling the broker whom it serves: once its client has made a request, since only then is there
/// anyone to name; again whenever a broker is met anew, since a restarted broker remembers nothing; and
/// without any call waiting on it or failing for it.
/// </summary>
public sealed class RelayIntroductionTests
{
	private const string Instructions = "Use the probe tools.";

	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

	[Test]
	public async Task The_client_is_introduced_once_it_has_asked_something(CancellationToken cancellationToken)
	{
		await using var broker = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker);
		var folder = Directory.GetCurrentDirectory();
		await using var relay = await TrayRelay.ConnectAsync(upstream, folder, NullLoggerFactory.Instance, Patience, cancellationToken);

		await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		upstream.Introductions.ShouldBeEmpty("nobody has asked anything, so there is nobody to name");

		await using var front = await RelayFront.OpenAsync(relay, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);
		await front.Agent.ListToolsAsync(cancellationToken: cancellationToken);

		var (reached, client) = await FirstAsync(upstream, cancellationToken);
		reached.ShouldBe(broker);
		client.ClientName.ShouldBe("claude-code");
		client.ClientVersion.ShouldBe("2.1.300");
		client.WorkingDirectory.ShouldBe(folder);
		client.ToolsCurrent.ShouldBe(true);
		client.HeldTools.ShouldNotBeNull().Length.ShouldBe(64);
		CallSession.Relayed(client.Session).ShouldNotBeNull("the session it names is the one its calls carry");
	}

	/// <summary>A client on a revision that still has roots says them, and the first root is what the branch is read from.</summary>
	[Test]
	public async Task A_client_that_has_roots_is_introduced_with_them(CancellationToken cancellationToken)
	{
		await using var broker = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);

		var root = Path.Combine(Path.GetTempPath(), "rose-roots", "Shop");
		await using var front = await RelayFront.OpenAsync(
			relay, RelayFront.Options("claude-code", "2.1.300", "2025-11-25", root), cancellationToken);

		await front.Agent.ListToolsAsync(cancellationToken: cancellationToken);

		var (_, client) = await FirstAsync(upstream, cancellationToken);
		client.Roots.ShouldBe([root]);
		client.ClientName.ShouldBe("claude-code");
	}

	/// <summary>
	/// A restarted broker knows nothing of the session, so a reconnect introduces it again -- holding tools the new
	/// broker changed, until the client lists them, which is introduced again too.
	/// </summary>
	[Test]
	public async Task A_reconnect_introduces_the_session_again_and_says_whether_its_tools_are_current(CancellationToken cancellationToken)
	{
		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		await using var front = await RelayFront.OpenAsync(relay, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);

		await front.Agent.ListToolsAsync(cancellationToken: cancellationToken);
		var (_, before) = await FirstAsync(upstream, cancellationToken);

		await using var second = new FakeBroker([FakeBroker.ReadOnly("probe_read"), FakeBroker.ReadOnly("probe_new")], Instructions, Commit('b'));
		upstream.Current = second;
		await first.DropConnectionsAsync();

		var stale = await NextAsync(upstream, reached => reached.Broker == second, cancellationToken);
		stale.Client.Session.ShouldBe(before.Session, "it is the same session on a new broker");
		stale.Client.ToolsCurrent.ShouldBe(false, "the client holds the tools from before the restart");

		await front.Agent.ListToolsAsync(cancellationToken: cancellationToken);

		var current = await NextAsync(upstream, reached => reached.Client.ToolsCurrent == true && reached.Broker == second, cancellationToken);
		current.Client.HeldTools.ShouldNotBe(before.HeldTools);
	}

	/// <summary>An introduction the broker cannot take fails nothing the session asked for.</summary>
	[Test]
	public async Task A_failed_introduction_fails_no_call(CancellationToken cancellationToken)
	{
		await using var broker = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker) { IntroductionsFail = true };
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		await using var front = await RelayFront.OpenAsync(relay, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);

		(await front.Agent.ListToolsAsync(cancellationToken: cancellationToken)).ShouldHaveSingleItem();
		var result = await front.Agent.CallToolAsync("probe_read", cancellationToken: cancellationToken);

		result.IsError.ShouldNotBe(true);
		broker.Calls.ShouldContain("probe_read");
	}

	/// <summary>What the relay knows before its client has asked anything: its folder, its label and its client's process.</summary>
	[Test]
	public async Task The_facts_from_the_machine_are_known_before_the_client_speaks(CancellationToken cancellationToken)
	{
		var introduction = new ClientIntroduction(
			CallSession.Mint(), Directory.GetCurrentDirectory(), "  ", () => (1717, "claude"), NullLogger.Instance);

		introduction.Known.ShouldBeFalse();

		var client = await introduction.GatherAsync(new string('c', 64), toolsCurrent: true, cancellationToken);
		client.Label.ShouldBeNull("a blank label is no label");
		client.ClientProcessId.ShouldBe(1717);
		client.ClientProcessName.ShouldBe("claude");
		client.ClientName.ShouldBeNull();

		var labelled = new ClientIntroduction(
			CallSession.Mint(), Directory.GetCurrentDirectory(), "card 5.4", () => null, NullLogger.Instance);
		(await labelled.GatherAsync(null, null, cancellationToken)).Label.ShouldBe("card 5.4");
	}

	private static string Commit(char digit) => new(digit, 40);

	private static Task<(FakeBroker? Broker, RelayClient Client)> FirstAsync(FakeUpstream upstream, CancellationToken cancellationToken) =>
		NextAsync(upstream, _ => true, cancellationToken);

	private static async Task<(FakeBroker? Broker, RelayClient Client)> NextAsync(
		FakeUpstream upstream,
		Func<(FakeBroker? Broker, RelayClient Client), bool> matching,
		CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (true)
		{
			var found = upstream.Introductions.Where(matching).Select(entry => ((FakeBroker? Broker, RelayClient Client)?)entry).LastOrDefault();
			if (found is { } introduction) return introduction;

			if (DateTime.UtcNow > deadline) throw new ShouldAssertException("Timed out waiting for the relay to introduce its session.");

			await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
		}
	}
}
