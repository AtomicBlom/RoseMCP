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
		TraySummary.Tooltip(Unstamped, [Evicted()], sessions: 0, running: 0).ShouldBe("RoseMCP 1.3.0 - nothing loaded");
		TraySummary.Tooltip(Unstamped, [Loaded(), Evicted()], sessions: 0, running: 0).ShouldBe("RoseMCP 1.3.0 - 1 solution, idle");
		TraySummary.Tooltip(Unstamped, [Loaded(), Loaded()], sessions: 1, running: 3).ShouldBe("RoseMCP 1.3.0 - 2 solutions, 1 session, 3 running");
	}

	/// <summary>
	/// The icon says which build is running, by its short commit, because two local builds share a
	/// version and the commit is what tells a fresh deploy from the one before it.
	/// </summary>
	[Test]
	public void The_tooltip_names_the_build_by_its_commit()
	{
		var build = Unstamped with { Commit = "c11d75a16a82274f8c52a9e33110afee4bdecc0a", Dirty = false };

		TraySummary.Tooltip(build, [], sessions: 0, running: 0).ShouldBe("RoseMCP 1.3.0 (c11d75a) - nothing loaded");
		TraySummary.Tooltip(build with { Dirty = true }, [], sessions: 0, running: 0)
			.ShouldBe("RoseMCP 1.3.0 (c11d75a, modified) - nothing loaded");
	}

	/// <summary>
	/// Windows keeps 127 characters of a notification icon's tooltip, so a long version is cut here
	/// rather than wherever the shell decides.
	/// </summary>
	[Test]
	public void The_tooltip_fits_a_notification_icon()
	{
		var build = Unstamped with { Version = "1.3.0-" + new string('x', 200) };

		TraySummary.Tooltip(build, [Loaded()], sessions: 0, running: 0).Length.ShouldBe(TraySummary.TooltipLimit);
	}

	/// <summary>A build stamped with nothing but a version, as an archive build is.</summary>
	private static readonly BuildIdentity Unstamped = new() { Version = "1.3.0" };

	/// <summary>A crash needs a look; an eviction is the broker doing its job and does not.</summary>
	[Test]
	public void The_subtitle_flags_a_crash_and_not_an_eviction()
	{
		TraySummary.Subtitle([Evicted()], NoSessions, running: 0).ShouldNotContain("attention");
		TraySummary.Subtitle([Crashed()], NoSessions, running: 0).ShouldContain("1 needs attention");
		TraySummary.Subtitle([], NoSessions, running: 0).ShouldBe("Waiting for a client to ask about one.");
	}

	/// <summary>
	/// A stopped row's process is gone, and its id may by now be another process's, so whatever
	/// working set it carries is not memory this broker holds and is left out of the total.
	/// </summary>
	[Test]
	public void The_subtitle_counts_memory_only_for_live_rows()
	{
		const long Megabyte = 1024 * 1024;
		var live = Loaded() with { WorkingSetBytes = 300 * Megabyte };
		var stopped = Evicted() with { WorkingSetBytes = 5000 * Megabyte };

		TraySummary.Subtitle([live, stopped], NoSessions, running: 0).ShouldStartWith("300 MB working set");
	}
}
