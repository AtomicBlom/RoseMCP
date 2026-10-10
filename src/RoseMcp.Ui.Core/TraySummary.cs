using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core;

/// <summary>
/// The tray's own lines about everything it holds: the headline and subtitle at the top of its
/// window, and the tooltip on its icon. Here rather than beside the window so what they say about
/// each mix of rows can be tested without one.
/// <para>
/// A stopped row -- evicted, or crashed -- stays listed so a person can read why it stopped, and it
/// holds no memory. These lines count it apart from the loaded ones rather than as one of them, so a
/// tray holding nothing says so however many rows it shows.
/// </para>
/// </summary>
public static class TraySummary
{
	/// <summary>
	/// The one line to read: how much is loaded, whether it is up yet, how many workers have stopped,
	/// and how many targets are being debugged.
	/// </summary>
	public static string Headline(
		IReadOnlyList<WorkspaceSummary> workspaces,
		IReadOnlyList<LiveAppSessionSummary> sessions)
	{
		var debugging = sessions.Count > 0 ? Format.Count(sessions.Count, "session") : null;

		if (workspaces.Count == 0)
		{
			return debugging is null ? "Nothing loaded" : $"Debugging {debugging}";
		}

		var live = workspaces.Where(summary => summary.Alive).ToList();
		var stopped = workspaces.Count - live.Count;

		var solutions = Format.Count(live.Count, "solution");
		var allLoading = live.All(summary => summary.State == WorkspaceState.Loading);

		var loaded = $"{solutions} loaded";
		if (live.Count == 0) loaded = "Nothing loaded";
		else if (allLoading) loaded = $"Loading {solutions}";

		if (stopped > 0) loaded += $", {Format.Count(stopped, "stopped worker")}";

		return debugging is null ? loaded : $"{loaded}, debugging {debugging}";
	}

	/// <summary>What it costs, whether it is busy, and whether anything below needs a look.</summary>
	public static string Subtitle(
		IReadOnlyList<WorkspaceSummary> workspaces,
		IReadOnlyList<LiveAppSessionSummary> sessions,
		int running)
	{
		if (workspaces.Count == 0 && sessions.Count == 0) return "Waiting for a client to ask about one.";

		var parts = new List<string>();

		if (workspaces.Count > 0)
		{
			// Live rows only: a stopped row's process is gone, and whatever figure it carries is not
			// memory this broker holds.
			var workingSet = workspaces.Where(summary => summary.Alive).Sum(summary => summary.WorkingSetBytes ?? 0);
			parts.Add($"{Format.Bytes(workingSet)} working set");
		}

		parts.Add(running == 0 ? "idle" : $"{Format.Count(running, "operation")} running");

		var troubled = workspaces.Count(summary => summary.State is WorkspaceState.Degraded or WorkspaceState.Faulted);
		if (troubled > 0) parts.Add(troubled == 1 ? "1 needs attention" : $"{troubled} need attention");

		// A held target is the one state here somebody has to end: an app frozen by a debugger stays
		// frozen until its safety timer or a person lets it go.
		var held = sessions.Count(summary => summary.Stop is not null);
		if (held > 0) parts.Add($"{Format.Count(held, "target")} stopped");

		return string.Join(Format.Separator, parts);
	}

	/// <summary>
	/// Kept to a few words: this is read hovering over a 16-pixel icon, and it is the only view of
	/// the broker available without opening the window. Counts loaded solutions only, because the
	/// icon says what is in memory and a stopped row holds none.
	/// <para>
	/// It names the build first, version and short commit, because "which build is running" is the
	/// question a person asks of the icon after every deploy, and two local builds share a version.
	/// Cut to <see cref="TooltipLimit"/>, past which Windows drops the tooltip's tail itself.
	/// </para>
	/// </summary>
	public static string Tooltip(BuildIdentity build, IReadOnlyList<WorkspaceSummary> workspaces, int sessions, int running)
	{
		var loaded = workspaces.Count(summary => summary.Alive);
		var name = $"RoseMCP {build.Version}";

		if (build.ShortCommit() is { } commit) name += build.Dirty == true ? $" ({commit}, modified)" : $" ({commit})";

		var parts = new List<string>();

		if (loaded > 0) parts.Add(Format.Count(loaded, "solution"));
		if (sessions > 0) parts.Add(Format.Count(sessions, "session"));

		if (parts.Count == 0) return Cut($"{name} - nothing loaded");

		parts.Add(running == 0 ? "idle" : $"{running} running");

		return Cut($"{name} - {string.Join(", ", parts)}");
	}

	/// <summary>
	/// The longest tooltip a notification icon shows: <c>NOTIFYICONDATA.szTip</c> holds 128
	/// characters, the terminator included.
	/// </summary>
	public const int TooltipLimit = 127;

	private static string Cut(string tooltip) => tooltip.Length <= TooltipLimit ? tooltip : tooltip[..TooltipLimit];
}
