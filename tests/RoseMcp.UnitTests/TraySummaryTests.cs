using RoseMcp.Contracts;
using RoseMcp.Ui.Core;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the tray says at the top of its window and on its icon, for each mix of rows it can hold.
/// <para>
/// The case worth pinning is the stopped row. An evicted or crashed worker stays listed so a person
/// can read why it stopped, and it holds no memory, so counting it as loaded would have a tray that
/// freed everything still claiming to hold it.
/// </para>
/// </summary>
public sealed class TraySummaryTests
{
	private static readonly IReadOnlyList<LiveAppSessionSummary> NoSessions = [];

	private static WorkspaceSummary Row(WorkspaceState state, bool alive = true, string? exitReason = null) => new()
	{
		Workspace = $"D:/repo/{Guid.NewGuid():N}.slnx",
		DisplayName = "Repo",
		Alive = alive,
		ExitReason = exitReason ?? (alive ? "Running" : "Evicted"),
		State = state,
		StartedUtc = DateTime.UtcNow,
		Uptime = TimeSpan.FromMinutes(5),
	};

	private static WorkspaceSummary Loaded() => Row(WorkspaceState.Loaded);

	private static WorkspaceSummary Loading() => Row(WorkspaceState.Loading);

	private static WorkspaceSummary Evicted() => Row(WorkspaceState.Unloaded, alive: false);

	private static WorkspaceSummary Crashed() => Row(WorkspaceState.Faulted, alive: false, exitReason: "Crashed");

	private static LiveAppSessionSummary Session() => new()
	{
		SessionId = "session-abcd1234",
		TargetDescription = "Widget.exe (launched)",
		Architecture = TargetArchitecture.X64,
		State = LiveAppSessionState.Ready,
		StartedUtc = DateTime.UtcNow,
		Uptime = TimeSpan.FromMinutes(1),
		Execution = LiveExecutionState.Running,
		XamlStack = XamlStack.WinUi,
		XamlStackReason = "it has loaded Microsoft.UI.Xaml.dll",
		XamlProvider = LiveXamlProvider.None,
	};

	[Test]
	public void Says_nothing_is_loaded_when_there_are_no_rows()
	{
		TraySummary.Headline([], NoSessions).ShouldBe("Nothing loaded");
		TraySummary.Headline([], [Session()]).ShouldBe("Debugging 1 session");
	}

	[Test]
	public void Counts_loaded_solutions_and_says_when_all_are_still_loading()
	{
		TraySummary.Headline([Loaded(), Loading()], NoSessions).ShouldBe("2 solutions loaded");
		TraySummary.Headline([Loading(), Loading()], NoSessions).ShouldBe("Loading 2 solutions");
		TraySummary.Headline([Loaded()], [Session()]).ShouldBe("1 solution loaded, debugging 1 session");
	}

	/// <summary>
	/// A tray whose every worker has been evicted holds nothing, and says so before it says how many
	/// rows are left to read.
	/// </summary>
	[Test]
	public void Counts_stopped_workers_apart_from_loaded_ones()
	{
		TraySummary.Headline([Evicted()], NoSessions).ShouldBe("Nothing loaded, 1 stopped worker");
		TraySummary.Headline([Evicted(), Crashed()], [Session()])
			.ShouldBe("Nothing loaded, 2 stopped workers, debugging 1 session");
		TraySummary.Headline([Loaded(), Evicted()], NoSessions).ShouldBe("1 solution loaded, 1 stopped worker");
	}

	/// <summary>A stopped row is not loading, so it cannot stop the live ones reading as loading.</summary>
	[Test]
	public void A_stopped_row_does_not_change_whether_the_live_ones_are_loading()
	{
		TraySummary.Headline([Loading(), Evicted()], NoSessions).ShouldBe("Loading 1 solution, 1 stopped worker");
	}

	[Test]
	public void The_tooltip_counts_only_what_is_in_memory()
	{
		TraySummary.Tooltip([Evicted()], sessions: 0, running: 0).ShouldBe("RoseMCP - nothing loaded");
		TraySummary.Tooltip([Loaded(), Evicted()], sessions: 0, running: 0).ShouldBe("RoseMCP - 1 solution, idle");
		TraySummary.Tooltip([Loaded(), Loaded()], sessions: 1, running: 3).ShouldBe("RoseMCP - 2 solutions, 1 session, 3 running");
	}

	/// <summary>A crash needs a look; an eviction is the broker doing its job and does not.</summary>
	[Test]
	public void The_subtitle_flags_a_crash_and_not_an_eviction()
	{
		TraySummary.Subtitle([Evicted()], NoSessions, running: 0).ShouldNotContain("attention");
		TraySummary.Subtitle([Crashed()], NoSessions, running: 0).ShouldContain("1 needs attention");
		TraySummary.Subtitle([], NoSessions, running: 0).ShouldBe("Waiting for a client to ask about one.");
	}
}
