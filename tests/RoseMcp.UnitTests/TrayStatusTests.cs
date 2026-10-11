using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Tray;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the tray icon shows for each mix of facts: which of its three states, what its tooltip says, and
/// what its one menu holds.
/// <para>
/// The cases worth pinning are the ones a person would read wrongly: a broker that cannot take calls must
/// never show as serving whatever else is true, a stopped worker holds no memory and is not counted, and the
/// menu offers an item only when there is something behind it.
/// </para>
/// </summary>
public sealed class TrayStatusTests
{
	/// <summary>A build stamped with nothing but a version, as an archive build is.</summary>
	private static readonly BuildIdentity Unstamped = new() { Version = "1.3.0" };

	private static readonly TrayFacts Serving = new() { Build = Unstamped };

	private static readonly StartupState Off = new(Enabled: false, Elsewhere: false, Refused: false);

	[Test]
	public void Not_serving_outranks_everything_waiting()
	{
		TrayStatus.StateOf(Serving).ShouldBe(TrayIconState.Serving);
		TrayStatus.StateOf(Serving with { UpdateVersion = "1.4.0" }).ShouldBe(TrayIconState.NeedsYou);
		TrayStatus.StateOf(Serving with { NotServing = "starting", UpdateVersion = "1.4.0", Failure = "boom" })
			.ShouldBe(TrayIconState.NotServing);
	}

	/// <summary>Each thing the design names as waiting for the person puts the icon in that state; a checkout at the build does not.</summary>
	[Test]
	public void Each_thing_waiting_needs_the_person()
	{
		TrayStatus.StateOf(Serving with { Failure = "boom" }).ShouldBe(TrayIconState.NeedsYou);
		TrayStatus.StateOf(Serving with { NotesAnswered = 1 }).ShouldBe(TrayIconState.NeedsYou);
		TrayStatus.StateOf(Serving with { CheckoutAhead = 2 }).ShouldBe(TrayIconState.NeedsYou);
		TrayStatus.StateOf(Serving with { CheckoutAhead = 0 }).ShouldBe(TrayIconState.Serving);
		TrayStatus.StateOf(Serving with { CheckoutAhead = null }).ShouldBe(TrayIconState.Serving);
	}

	[Test]
	public void What_is_waiting_is_listed_most_pressing_first()
	{
		var all = Serving with { Failure = "the disk is full", NotesAnswered = 2, UpdateVersion = "1.4.0", CheckoutAhead = 1 };

		TrayStatus.NeedsYou(all).Select(need => (need.Action, need.Text)).ShouldBe(
		[
			(TrayAction.OpenFailureLog, "Rose failed: the disk is full"),
			(TrayAction.OpenNoteAnswers, "2 answers to your notes"),
			(TrayAction.OpenRelease, "RoseMCP 1.4.0 is available"),
			(TrayAction.OpenCheckout, "Your checkout is 1 commit ahead"),
		]);
	}

	/// <summary>The tooltip gives the build and how many agents, workspaces and debugged apps there are, always all three.</summary>
	[Test]
	public void The_tooltip_names_the_build_and_counts_what_it_serves()
	{
		TrayStatus.Tooltip(Serving).ShouldBe("RoseMCP 1.3.0\n0 agents, 0 workspaces, 0 debugged apps");
		TrayStatus.Tooltip(Serving with { Agents = 1, Workspaces = 2, DebuggedApps = 1 })
			.ShouldBe("RoseMCP 1.3.0\n1 agent, 2 workspaces, 1 debugged app");
	}

	/// <summary>
	/// The icon says which build is running, by its short commit, because two local builds share a version
	/// and the commit is what tells a fresh deploy from the one before it.
	/// </summary>
	[Test]
	public void The_tooltip_names_the_build_by_its_commit()
	{
		var build = Unstamped with { Commit = "c11d75a16a82274f8c52a9e33110afee4bdecc0a", Dirty = false };

		TrayStatus.Tooltip(Serving with { Build = build }).ShouldStartWith("RoseMCP 1.3.0 (c11d75a)\n");
		TrayStatus.Tooltip(Serving with { Build = build with { Dirty = true } }).ShouldStartWith("RoseMCP 1.3.0 (c11d75a, modified)\n");
	}

	[Test]
	public void The_tooltip_names_what_is_waiting_after_the_counts()
	{
		TrayStatus.Tooltip(Serving with { UpdateVersion = "1.4.0" })
			.ShouldBe("RoseMCP 1.3.0\n0 agents, 0 workspaces, 0 debugged apps\nRoseMCP 1.4.0 is available");
	}

	/// <summary>
	/// Not serving says why in place of the counts, and a start that failed is said once, as why, rather than
	/// again as a failure waiting to be read.
	/// </summary>
	[Test]
	public void The_tooltip_says_why_it_is_not_serving()
	{
		TrayStatus.Tooltip(Serving with { NotServing = "starting" }).ShouldBe("RoseMCP 1.3.0\nNot serving: starting");

		var failed = Serving with { NotServing = "it could not start: port 5077 is in use", Failure = "port 5077 is in use" };
		TrayStatus.Tooltip(failed).ShouldBe("RoseMCP 1.3.0\nNot serving: it could not start: port 5077 is in use");
	}

	/// <summary>
	/// Windows keeps 127 characters of a notification icon's tooltip, so a long one is cut here rather than
	/// wherever the shell decides.
	/// </summary>
	[Test]
	public void The_tooltip_fits_a_notification_icon()
	{
		var crowded = Serving with
		{
			Build = Unstamped with { Version = "1.3.0-" + new string('x', 60) },
			Failure = new string('f', 200),
			UpdateVersion = "1.4.0",
		};

		TrayStatus.Tooltip(crowded).Length.ShouldBe(TrayStatus.TooltipLimit);
	}

	/// <summary>A failure's message is one line in the menu, cut, since the log it opens has the rest.</summary>
	[Test]
	public void A_failure_is_one_line_in_the_menu()
	{
		var need = TrayStatus.NeedsYou(Serving with { Failure = "first line\nsecond line" }).Single();
		need.Text.ShouldBe("Rose failed: first line");

		var cut = TrayStatus.NeedsYou(Serving with { Failure = new string('x', 300) }).Single();
		cut.Text.Length.ShouldBe("Rose failed: ".Length + 80);
		cut.Text.ShouldEndWith("\u2026");
	}

	/// <summary>The menu the design draws, with nothing waiting: open, debug, then the three that are always there.</summary>
	[Test]
	public void The_menu_with_nothing_waiting()
	{
		var menu = TrayStatus.Menu(Serving, Off, roseAppInstalled: true);

		menu.Select(entry => entry.Action).ShouldBe(
		[
			TrayAction.OpenRose,
			TrayAction.DebugAnApp,
			TrayAction.Separator,
			TrayAction.StartWithWindows,
			TrayAction.OpenLogFolder,
			TrayAction.Exit,
		]);

		menu.Select(entry => entry.Text).ShouldBe(
			["Open RoseMCP", "Debug an app\u2026", string.Empty, "Start with Windows", "Open the log folder", "Exit"]);

		menu.ShouldAllBe(entry => entry.Enabled);
	}

	/// <summary>The update and checkout items appear only when there is one, between their own separators.</summary>
	[Test]
	public void The_menu_carries_what_is_waiting_between_separators()
	{
		var menu = TrayStatus.Menu(Serving with { UpdateVersion = "1.4.0", CheckoutAhead = 2 }, Off, roseAppInstalled: true);

		menu.Select(entry => entry.Action).ShouldBe(
		[
			TrayAction.OpenRose,
			TrayAction.DebugAnApp,
			TrayAction.Separator,
			TrayAction.OpenRelease,
			TrayAction.OpenCheckout,
			TrayAction.Separator,
			TrayAction.StartWithWindows,
			TrayAction.OpenLogFolder,
			TrayAction.Exit,
		]);
	}

	/// <summary>Only the startup item toggles, and it is ticked exactly when Windows starts this executable.</summary>
	[Test]
	public void The_startup_item_says_what_Windows_will_do()
	{
		Startup(Off).ShouldBe(new TrayMenuEntry(TrayAction.StartWithWindows, "Start with Windows", Checked: false));
		Startup(Off with { Enabled = true }).ShouldBe(new TrayMenuEntry(TrayAction.StartWithWindows, "Start with Windows", Checked: true));
		Startup(Off with { Elsewhere = true }).Text.ShouldBe("Start with Windows (registered elsewhere)");
		Startup(Off with { Elsewhere = true, Refused = true }).Text.ShouldBe("Start with Windows (Windows refused)");

		TrayStatus.Menu(Serving, Off, roseAppInstalled: true).Count(entry => entry.Checked is not null).ShouldBe(1);

		static TrayMenuEntry Startup(StartupState state) =>
			TrayStatus.Menu(Serving, state, roseAppInstalled: true).Single(entry => entry.Action == TrayAction.StartWithWindows);
	}

	/// <summary>
	/// An item that could not work is offered disabled with the reason, rather than hidden or left to fail; the
	/// way out and the log are always there, since those are what a person needs when it is not serving.
	/// </summary>
	[Test]
	public void What_cannot_open_says_why()
	{
		var missing = TrayStatus.Menu(Serving, Off, roseAppInstalled: false);
		missing[0].ShouldBe(new TrayMenuEntry(TrayAction.OpenRose, "Open RoseMCP (not installed)", Enabled: false));
		missing[1].ShouldBe(new TrayMenuEntry(TrayAction.DebugAnApp, "Debug an app\u2026 (not installed)", Enabled: false));

		var stopped = TrayStatus.Menu(Serving with { NotServing = "starting" }, Off, roseAppInstalled: true);
		stopped[0].ShouldBe(new TrayMenuEntry(TrayAction.OpenRose, "Open RoseMCP (not serving)", Enabled: false));
		stopped.Where(entry => entry.Action is TrayAction.OpenLogFolder or TrayAction.Exit).ShouldAllBe(entry => entry.Enabled);
	}

	/// <summary>
	/// An agent is a relayed session whose relay is watching, or a client with no relay. A relay that let go is
	/// within the broker's grace for it coming back, and is not connected now.
	/// </summary>
	[Test]
	public void Counts_agents_that_are_connected_now()
	{
		ClientSummary[] clients =
		[
			Client(named: true, watching: true),
			Client(named: true, watching: false),
			Client(named: false, watching: null),
		];

		TrayStatus.AgentsIn(clients).ShouldBe(2);
	}

	/// <summary>A stopped worker stays listed so a person can read why, and holds no memory, so it is not a workspace being served.</summary>
	[Test]
	public void Counts_only_live_workspaces()
	{
		WorkspaceSummary[] workspaces = [Workspace(alive: true), Workspace(alive: false)];

		var facts = Serving.WithCounts([], workspaces, [Session()]);

		facts.Workspaces.ShouldBe(1);
		facts.DebuggedApps.ShouldBe(1);
		facts.Agents.ShouldBe(0);
	}

	internal static ClientSummary Client(bool named, bool? watching, bool? toolsCurrent = null, string? heldTools = null, string? id = null) => new()
	{
		Id = id ?? Guid.NewGuid().ToString("N"),
		Named = named,
		ConnectedUtc = DateTimeOffset.UtcNow,
		Watching = watching,
		ToolsCurrent = toolsCurrent,
		HeldTools = heldTools,
	};

	private static WorkspaceSummary Workspace(bool alive) => new()
	{
		Workspace = $"D:/repo/{Guid.NewGuid():N}.slnx",
		DisplayName = "Repo",
		Alive = alive,
		ExitReason = alive ? "Running" : "Evicted",
		State = alive ? WorkspaceState.Loaded : WorkspaceState.Unloaded,
		StartedUtc = DateTime.UtcNow,
		Uptime = TimeSpan.FromMinutes(5),
	};

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
}
