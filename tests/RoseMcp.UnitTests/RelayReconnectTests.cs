using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the relay does when the broker behind it is replaced, which is what every deploy does to it.
/// <para>
/// A relay that died with the tray would cost every session a <c>/mcp</c> on every deploy, even when
/// nothing it called had changed. So the relay outlives the deploy, and these hold the three things
/// that make that worth having: it finds the new broker without being asked, it tells the client to
/// list the tools again exactly when the tools or instructions differ, and the first result afterwards
/// says once what happened.
/// </para>
/// </summary>
public sealed class RelayReconnectTests
{
	private const string Instructions = "Use the probe tools.";

	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

	/// <summary>
	/// The same tools and instructions on the new broker: the client is told nothing at the protocol
	/// level, and the next result -- one, not every one -- carries the line naming the new build.
	/// </summary>
	[Test]
	public async Task A_restart_with_the_same_tools_sends_no_list_changed_and_says_so_once()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		var changes = Count(relay);

		await relay.ListToolsAsync(cancellationToken);

		await using var second = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('b'));
		await Replace(upstream, first, second, cancellationToken);

		changes.Raised.ShouldBe(0, "the client holds every tool the new broker has, so it has nothing to refresh");

		var notices = Notices(await Call(relay, "probe_read", cancellationToken));
		var line = notices.ShouldHaveSingleItem();
		line.ShouldStartWith("RoseMCP restarted and is now build 1.3.0 at bbbbbbb", Case.Sensitive);
		line.ShouldContain("load again on the first call that needs them", Case.Sensitive);
		line.ShouldNotContain("tools changed", Case.Sensitive);

		Notices(await Call(relay, "probe_read", cancellationToken)).ShouldBeEmpty("the restart is said once");
	}

	/// <summary>
	/// Different tools on the new broker: one <c>list_changed</c>, and the line names what changed.
	/// Once the client has listed them, a later broker with those same tools is not a change.
	/// </summary>
	[Test]
	public async Task A_restart_with_other_tools_sends_one_list_changed_and_names_them()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read"), FakeBroker.ReadOnly("probe_gone")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		var changes = Count(relay);

		await relay.ListToolsAsync(cancellationToken);

		Tool[] newer = [FakeBroker.ReadOnly("probe_read", "Reads something else."), FakeBroker.ReadOnly("probe_new")];
		await using var second = new FakeBroker(newer, Instructions, Commit('b'));
		await Replace(upstream, first, second, cancellationToken);

		changes.Raised.ShouldBe(1);

		var line = Notices(await Call(relay, "probe_read", cancellationToken)).ShouldHaveSingleItem();
		line.ShouldContain("its tools changed (added probe_new; removed probe_gone; changed probe_read)", Case.Sensitive);

		// The client lists again, as list_changed asks it to; a broker with the same tools is then no change.
		await relay.ListToolsAsync(cancellationToken);

		await using var third = new FakeBroker(newer, Instructions, Commit('c'));
		await Replace(upstream, second, third, cancellationToken);

		changes.Raised.ShouldBe(1, "the client already holds the tools the third broker has");
	}

	/// <summary>
	/// Instructions reach a client only when it connects, so a change to them alone still sends
	/// <c>list_changed</c> -- the one signal there is -- and the line says a reconnect picks them up.
	/// Said once: a later broker with the same new instructions is not a second change.
	/// </summary>
	[Test]
	public async Task Changed_instructions_alone_send_list_changed_once_and_say_to_reconnect()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		var changes = Count(relay);

		relay.Instructions.ShouldBe(Instructions, "the client is given the broker's instructions as the relay's own");

		await using var second = new FakeBroker([FakeBroker.ReadOnly("probe_read")], "Use the probe tools, carefully.", Commit('b'));
		await Replace(upstream, first, second, cancellationToken);

		changes.Raised.ShouldBe(1);
		var line = Notices(await Call(relay, "probe_read", cancellationToken)).ShouldHaveSingleItem();
		line.ShouldContain("server instructions changed", Case.Sensitive);
		line.ShouldContain("(/mcp)", Case.Sensitive);
		line.ShouldNotContain("its tools changed", Case.Sensitive);

		await using var third = new FakeBroker([FakeBroker.ReadOnly("probe_read")], "Use the probe tools, carefully.", Commit('c'));
		await Replace(upstream, second, third, cancellationToken);

		changes.Raised.ShouldBe(1, "the client was told once, which is all the protocol allows");
	}

	/// <summary>
	/// The debug sessions this session had open die with the broker, and the line names them -- read
	/// while the broker was alive, after the call that started one, since the new broker never heard
	/// of them.
	/// </summary>
	[Test]
	public async Task A_restart_names_the_debug_sessions_that_ended_with_it()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var tools = FakeBroker.WithDebugging(FakeBroker.ReadOnly("probe_read"));
		await using var first = new FakeBroker(tools, Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);

		await Call(relay, ToolNames.DebugAttach, cancellationToken);
		first.Calls.ShouldContain(ToolNames.DebugList, "the relay reads the open sessions after a call that starts one");

		await using var second = new FakeBroker(tools, Instructions, Commit('b'));
		await Replace(upstream, first, second, cancellationToken);

		var line = Notices(await Call(relay, "probe_read", cancellationToken)).ShouldHaveSingleItem();
		line.ShouldContain("the debug sessions this session had open ended with it: session-0001 (Probe.exe)", Case.Sensitive);
	}

	/// <summary>
	/// A connection lost to a broker that is still there is not a restart, and the line does not say
	/// one happened: solutions are still loaded. The debug sessions belonged to the old connection,
	/// though, and that is said.
	/// </summary>
	[Test]
	public async Task A_dropped_connection_to_the_same_broker_is_not_called_a_restart()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var tools = FakeBroker.WithDebugging(FakeBroker.ReadOnly("probe_read"));
		await using var broker = new FakeBroker(tools, Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		var changes = Count(relay);

		await Call(relay, ToolNames.DebugAttach, cancellationToken);
		await broker.DropConnectionsAsync();
		await Eventually(() => upstream.Connections == 2 && upstream.Watching is not null, "the relay reconnects to the same broker", cancellationToken);

		changes.Raised.ShouldBe(0);

		var line = Notices(await Call(relay, "probe_read", cancellationToken)).ShouldHaveSingleItem();
		line.ShouldStartWith("The connection to RoseMCP (build 1.3.0 at aaaaaaa) was opened again", Case.Sensitive);
		line.ShouldNotContain("restarted", Case.Sensitive);
		line.ShouldContain("belong to the old connection and cannot be reached from this one: session-0001 (Probe.exe)", Case.Sensitive);
	}

	/// <summary>
	/// Every call in flight fails at once when the broker goes. They and the watch share one
	/// reconnect, the read-only ones are re-sent to the new broker, and exactly one of their results
	/// carries the line.
	/// </summary>
	[Test]
	public async Task Concurrent_calls_through_a_restart_share_one_reconnect_and_one_notice()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		var changes = Count(relay);

		upstream.Current = null;
		await first.DropConnectionsAsync();

		var calls = Enumerable.Range(0, 8).Select(_ => Call(relay, "probe_read", cancellationToken)).ToList();

		await using var second = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('b'));
		await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
		upstream.Current = second;

		var results = await Task.WhenAll(calls);

		results.Count(result => Notices(result).Count > 0).ShouldBe(1, "the restart is said on one result, whichever finished first");
		upstream.Connections.ShouldBe(2, "one reconnect, shared by the calls and the watch");
		changes.Raised.ShouldBe(0);
	}

	/// <summary>
	/// A watch can end with its broker still there. The relay asks before believing it, because
	/// reconnecting to a live broker opens a new connection and the old one's debug sessions with it.
	/// </summary>
	[Test]
	public async Task A_watch_that_ends_while_the_broker_answers_does_not_reconnect()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var broker = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker);
		upstream.EndNextWatchEarly();

		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);

		// Long enough for the ended watch to be noticed, the broker asked, and the watch opened again.
		await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

		upstream.Connections.ShouldBe(1);
		Notices(await Call(relay, "probe_read", cancellationToken)).ShouldBeEmpty();
	}

	/// <summary>
	/// A watch that reaches another broker than the one the relay is connected to reconnects at once,
	/// without asking the old connection first. A broker restarted on the same endpoint answers that
	/// connection with a protocol error, which counts as an answer when a watch merely ended, so asking
	/// would keep the relay on the old connection for good, watching again every pause.
	/// </summary>
	[Test]
	public async Task A_watch_that_reaches_a_new_broker_reconnects_though_the_old_connection_answers()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		upstream.EndNextWatchEarly();

		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);
		var changes = Count(relay);

		// What the old connection meets once the broker has restarted under it: a refusal, not silence.
		await using (var probe = await first.ConnectAsync(cancellationToken))
		{
			first.Refusing = true;
			await Should.ThrowAsync<McpProtocolException>(() => probe.PingAsync(cancellationToken: cancellationToken).AsTask());
		}

		// Inside the pause after the early watch: the next watch reaches the new broker.
		await using var second = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('b'));
		upstream.Current = second;

		await Eventually(() => upstream.Watching == second, "the relay moves to the broker its watch reached", cancellationToken);

		var line = Notices(await Call(relay, "probe_read", cancellationToken)).ShouldHaveSingleItem();
		line.ShouldStartWith("RoseMCP restarted and is now build 1.3.0 at bbbbbbb", Case.Sensitive);

		upstream.Connections.ShouldBe(2, "one reconnect, to the broker the watch reached");
		changes.Raised.ShouldBe(0, "the new broker has the same tools");
		second.Calls.ShouldContain("probe_read", "the call after the reconnect is served by the new broker");
		Notices(await Call(relay, "probe_read", cancellationToken)).ShouldBeEmpty("the restart is said once");
	}

	/// <summary>
	/// A broker the relay started cannot be replaced, so losing it ends the relay rather than leaving
	/// it answering every call with the same failure.
	/// </summary>
	[Test]
	public async Task A_relay_whose_broker_cannot_be_replaced_ends_when_it_goes()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var broker = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker, reconnects: false);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);

		relay.Ended.IsCompleted.ShouldBeFalse();

		upstream.Current = null;
		await broker.DropConnectionsAsync();

		await relay.Ended.WaitAsync(Patience, cancellationToken);
		upstream.Connections.ShouldBe(1, "nothing was started in its place");
	}

	/// <summary>
	/// Closing the relay while it is still trying to reach a broker that is down ends the attempts
	/// promptly, rather than waiting out a reconnect window nobody is waiting for.
	/// </summary>
	[Test]
	public async Task Closing_the_relay_during_an_outage_stops_the_reconnect_attempts()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var broker = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(broker);
		var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);

		upstream.Current = null;
		await broker.DropConnectionsAsync();
		await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

		await relay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
	}

	/// <summary>A relay protocol the broker does not share is said, since no reconnect can mend it.</summary>
	[Test]
	public async Task A_broker_speaking_another_relay_protocol_is_said()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var first = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('a'));
		var upstream = new FakeUpstream(first);
		await using var relay = await TrayRelay.ConnectAsync(upstream, Directory.GetCurrentDirectory(), NullLoggerFactory.Instance, Patience, cancellationToken);

		await using var second = new FakeBroker([FakeBroker.ReadOnly("probe_read")], Instructions, Commit('b'))
		{
			RelayProtocol = RelayProtocol.Version + 1,
		};

		await Replace(upstream, first, second, cancellationToken);

		var line = Notices(await Call(relay, "probe_read", cancellationToken)).ShouldHaveSingleItem();
		line.ShouldContain($"relay protocol {RelayProtocol.Version} and RoseMCP speaks {RelayProtocol.Version + 1}", Case.Sensitive);
	}

	private static string Commit(char digit) => new(digit, 40);

	/// <summary>Kills one broker, puts another in its place, and waits for the relay to move its watch there by itself.</summary>
	private static async Task Replace(FakeUpstream upstream, FakeBroker old, FakeBroker replacement, CancellationToken cancellationToken)
	{
		upstream.Current = replacement;
		await old.DropConnectionsAsync();

		await Eventually(() => upstream.Watching == replacement, "the relay finds the new broker without a call", cancellationToken);
	}

	private static async Task Eventually(Func<bool> condition, string what, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (!condition())
		{
			if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"Timed out waiting until {what}.");

			await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
		}
	}

	private static Task<CallToolResult> Call(TrayRelay relay, string tool, CancellationToken cancellationToken) =>
		relay.CallToolAsync(new CallToolRequestParams { Name = tool }, client: null!, cancellationToken).AsTask();

	private static IReadOnlyList<string> Notices(CallToolResult result)
	{
		if (result.StructuredContent is not { } content || !content.TryGetProperty("notices", out var notices)) return [];

		return [.. notices.EnumerateArray().Select(notice => notice.GetString()!)];
	}

	/// <summary>Counts the times the relay told its client the tool list changed.</summary>
	private static Counter Count(TrayRelay relay)
	{
		var counter = new Counter();
		relay.ToolsChanged.Changed += (_, _) => counter.Increment();

		return counter;
	}

	private sealed class Counter
	{
		private int _raised;

		public int Raised => Volatile.Read(ref _raised);

		public void Increment() => Interlocked.Increment(ref _raised);
	}
}
