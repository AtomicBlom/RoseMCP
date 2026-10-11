using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// An agent session as the Agents view shows it: named by its label, else its folder; its calls found by its row and
/// then by the key they carry, which outlives a restart; what it is using; and the line that says whether every
/// session holds this broker's tools.
/// </summary>
public sealed class AgentCardTests
{
	private static readonly DateTimeOffset Noon = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// A relay keeps its session's key across a restart, where its row in the client table is new: so the calls it
	/// made before the restart are its calls too, and the restart sits among them. Another agent's calls are not.
	/// </summary>
	[Test]
	public void An_agents_calls_are_found_across_a_restart()
	{
		var client = Client("row-2", label: "ink width");
		var before = Call("before", Noon.AddMinutes(-30)) with { ClientId = "row-1", ClientKey = "key-a" };
		var restart = OperationRowTests.Started("restart", Noon.AddMinutes(-20));
		var after = Call("after", Noon.AddMinutes(-5)) with { ClientId = "row-2", ClientKey = "key-a" };
		var someoneElse = Call("other", Noon.AddMinutes(-4)) with { ClientId = "row-3", ClientKey = "key-b" };
		var earlyRestart = OperationRowTests.Started("early", Noon.AddHours(-2));

		var mine = AgentCard.EntriesOf(client, [before, restart, after, someoneElse, earlyRestart]);

		mine.Select(entry => entry.Id).ShouldBe(["before", "after", "restart"], ignoreOrder: true);
	}

	/// <summary>
	/// A card is named by the label its session was started with, else the folder it works in; one that reached the
	/// broker without the relay says so, since nothing names it.
	/// </summary>
	[Test]
	public void A_card_is_named_by_its_label_else_its_folder()
	{
		new AgentCard(Client("1", label: "ink width"), [], AgentContext.Empty, Noon).Title.ShouldBe("ink width");

		var byFolder = new AgentCard(Client("2", folder: @"D:\Personal\RoseMCP\worktrees\activity-log"), [], AgentContext.Empty, Noon);
		byFolder.Title.ShouldBe("activity-log");
		byFolder.Folder.ShouldBe(@"D:\Personal\RoseMCP\worktrees\activity-log");
		byFolder.Branch.ShouldBe("feature/x");
		byFolder.Mode.ShouldContain("Claude Code 2.1");
		byFolder.Mode.ShouldContain("through the relay");

		var unnamed = new AgentCard(Client("3", named: false), [], AgentContext.Empty, Noon);
		unnamed.IsUnnamed.ShouldBeTrue();
		unnamed.Title.ShouldBe("An HTTP client connected without the relay");
		unnamed.Mode.ShouldContain("without the relay");
	}

	/// <summary>A card lists its newest operations and says how many more the Activity view holds; it goes idle after a quiet spell.</summary>
	[Test]
	public void A_card_lists_its_newest_calls_and_says_when_it_is_idle()
	{
		var calls = Enumerable.Range(0, AgentCard.OperationLimit + 3)
			.Select(index => Call($"c{index}", Noon.AddMinutes(-60 + index)) with { ClientId = "1", ClientKey = "key" })
			.ToList();
		var client = Client("1", label: "busy") with { LastCallUtc = Noon.AddMinutes(-10) };

		var card = new AgentCard(client, calls, AgentContext.Empty, Noon);

		card.Operations.Count.ShouldBe(AgentCard.OperationLimit);
		card.Operations[0].Id.ShouldBe($"c{AgentCard.OperationLimit + 2}", "the newest first");
		card.More.ShouldBe("3 more in Activity");
		card.ActivityFilter.ShouldBe("key", "the key outlives a restart, so the Activity view finds both sides of one");
		card.Mode.ShouldContain("idle 10m");

		card.Tick(Noon.AddMinutes(-15).Add(AgentCard.IdleAfter));
		card.Mode.ShouldNotContain("idle");
	}

	/// <summary>
	/// What an agent is using is read from its calls: each workspace and each debug session once, newest first, with
	/// the state each is in now -- and a workspace that has closed since says so.
	/// </summary>
	[Test]
	public void What_an_agent_uses_comes_from_its_calls()
	{
		var calls = new[]
		{
			Call("a", Noon.AddMinutes(-3)) with { Workspace = @"D:\pdf\Drawboard.All.slnx" },
			Call("b", Noon.AddMinutes(-2)) with { DebugSession = "s1" },
			Call("c", Noon.AddMinutes(-1)) with { Workspace = @"D:\PDF\drawboard.all.slnx" },
			Call("d", Noon.AddMinutes(-4)) with { Workspace = @"D:\tools\Tools.slnx" },
		};

		var context = new AgentContext(
			[WorkspaceRowTests.Workspace(@"D:\pdf\Drawboard.All.slnx", WorkspaceState.Loaded)],
			[SessionRowTests.Session() with { SessionId = "s1", TargetDescription = "Drawboard PDF", TargetProcessId = 13968 }]);

		var chips = UsingChip.Of(calls, context);

		if (OperatingSystem.IsWindows())
		{
			chips.Select(chip => chip.Text).ShouldBe(
			[
				"Drawboard.All.slnx  ·  ready",
				"Drawboard PDF  ·  pid 13968  ·  running",
				"Tools.slnx  ·  closed",
			]);
		}

		chips.Count(chip => chip.Kind == UsingKind.App).ShouldBe(1);
	}

	[Test]
	public void The_heading_says_whether_every_session_holds_this_brokers_tools()
	{
		AgentCard.Summarise([]).ShouldBe("No sessions connected");
		AgentCard.Summarise([Client("1"), Client("2")]).ShouldBe("2 sessions connected  ·  all on this build");
		AgentCard.Summarise([Client("1"), Client("2", named: false) with { ToolsCurrent = false }])
			.ShouldBe("2 sessions connected  ·  1 session using tools from before the restart");
	}

	/// <summary>Connected sessions come first, then whichever called most recently.</summary>
	[Test]
	public void Cards_put_connected_and_recent_sessions_first()
	{
		var gone = Client("gone") with { Watching = false, LastCallUtc = Noon };
		var quiet = Client("quiet") with { LastCallUtc = Noon.AddHours(-1) };
		var busy = Client("busy") with { LastCallUtc = Noon.AddMinutes(-1) };

		AgentCard.Order([gone, quiet, busy]).Select(client => client.Id).ShouldBe(["busy", "quiet", "gone"]);
	}

	internal static ClientSummary Client(string id, string? label = null, string? folder = null, bool named = true) => new()
	{
		Id = id,
		Named = named,
		ConnectedUtc = Noon.AddHours(-3),
		Watching = named ? true : null,
		Label = label,
		WorkingDirectory = named ? folder : null,
		Branch = named ? "feature/x" : null,
		ClientName = "Claude Code",
		ClientVersion = "2.1",
	};

	private static ActivityEntry Call(string id, DateTimeOffset start) =>
		OperationRowTests.Call(id, start, start.AddSeconds(1)) with { ClientId = "1" };
}
