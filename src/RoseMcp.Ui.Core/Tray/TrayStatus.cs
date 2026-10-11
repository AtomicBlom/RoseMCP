using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// What the tray icon shows for a set of <see cref="TrayFacts"/>: its state, its tooltip, what is
/// waiting for the person, and its one menu. Each a pure function, so what the icon says about every
/// mix of facts is tested without an icon.
/// </summary>
public static class TrayStatus
{
	/// <summary>
	/// The longest tooltip a notification icon shows: <c>NOTIFYICONDATA.szTip</c> holds 128
	/// characters, the terminator included.
	/// </summary>
	public const int TooltipLimit = 127;

	/// <summary>How much of a failure's message a menu item or tooltip line carries; the log has the rest.</summary>
	private const int FailureExcerpt = 80;

	/// <summary>
	/// The agent sessions connected now: a client behind a relay whose relay is watching, and a client
	/// that reached the broker over http without one, which stays listed until it has been idle a while.
	/// A relay that let go is within its grace period, which is the broker waiting to see whether it comes
	/// back, and is not counted as connected.
	/// </summary>
	public static int AgentsIn(IReadOnlyList<ClientSummary> clients) =>
		clients.Count(client => !client.Named || client.Watching == true);

	/// <summary><paramref name="facts"/> with the counts the tooltip gives, read from the broker's own lists.</summary>
	public static TrayFacts WithCounts(
		this TrayFacts facts,
		IReadOnlyList<ClientSummary> clients,
		IReadOnlyList<WorkspaceSummary> workspaces,
		IReadOnlyList<LiveAppSessionSummary> sessions) => facts with
		{
			Agents = AgentsIn(clients),
			Workspaces = workspaces.Count(summary => summary.Alive),
			DebuggedApps = sessions.Count,
		};

	/// <summary>Not serving outranks needing the person: a broker that cannot take calls is the thing to know first.</summary>
	public static TrayIconState StateOf(TrayFacts facts)
	{
		if (facts.NotServing is not null) return TrayIconState.NotServing;

		return NeedsYou(facts).Count > 0 ? TrayIconState.NeedsYou : TrayIconState.Serving;
	}

	/// <summary>
	/// What is waiting for the person, most pressing first, each as the menu item that deals with it. The
	/// same sentences end the tooltip, so the icon and its menu name the same things.
	/// </summary>
	public static IReadOnlyList<TrayMenuEntry> NeedsYou(TrayFacts facts)
	{
		var needs = new List<TrayMenuEntry>();

		if (facts.Failure is { Length: > 0 } failure)
		{
			needs.Add(new TrayMenuEntry(TrayAction.OpenFailureLog, $"Rose failed: {Excerpt(failure, FailureExcerpt)}"));
		}

		if (facts.NotesAnswered > 0)
		{
			var answers = facts.NotesAnswered == 1
				? "An agent answered your note"
				: $"{facts.NotesAnswered} answers to your notes";

			needs.Add(new TrayMenuEntry(TrayAction.OpenNoteAnswers, answers));
		}

		if (facts.UpdateVersion is { Length: > 0 } version)
		{
			needs.Add(new TrayMenuEntry(TrayAction.OpenRelease, $"RoseMCP {version} is available"));
		}

		if (facts.CheckoutAhead is > 0 and var ahead)
		{
			needs.Add(new TrayMenuEntry(TrayAction.OpenCheckout, $"Your checkout is {Format.Count(ahead, "commit")} ahead"));
		}

		return needs;
	}

	/// <summary>
	/// The build first, version and short commit, because "which build is running" is the question a person
	/// asks of the icon after every deploy and two local builds share a version. Then how much it serves, or
	/// why it cannot, then what is waiting. Cut to <see cref="TooltipLimit"/>, past which Windows drops the
	/// tooltip's tail itself.
	/// <para>
	/// A failure that stopped the broker starting is said once, as why it is not serving, rather than again
	/// as a failure waiting to be read.
	/// </para>
	/// </summary>
	public static string Tooltip(TrayFacts facts)
	{
		var lines = new List<string> { Name(facts.Build) };

		var needs = NeedsYou(facts);

		if (facts.NotServing is { } why)
		{
			lines.Add($"Not serving: {why}");
			needs = [.. needs.Where(need => need.Action != TrayAction.OpenFailureLog)];
		}
		else
		{
			lines.Add($"{Format.Count(facts.Agents, "agent")}, {Format.Count(facts.Workspaces, "workspace")}, "
				+ Format.Count(facts.DebuggedApps, "debugged app"));
		}

		lines.AddRange(needs.Select(need => need.Text));

		var tooltip = string.Join('\n', lines);

		return tooltip.Length <= TooltipLimit ? tooltip : tooltip[..TooltipLimit];
	}

	/// <summary>
	/// The tray's one menu: open the RoseMCP app or debug an app with it; whatever is waiting for the person;
	/// then starting with Windows, the log folder, and Exit.
	/// <para>
	/// An item that cannot work is offered disabled with the reason in its text, rather than hidden or left to
	/// fail: the RoseMCP app is optional and a source tree without it built is ordinary, and a window opened on
	/// a broker that is not serving would only say it cannot connect.
	/// </para>
	/// </summary>
	/// <param name="facts">What the tray knows now.</param>
	/// <param name="startup">What Windows says about starting this tray at sign-in.</param>
	/// <param name="roseAppInstalled">Whether the RoseMCP app's executable was found.</param>
	public static IReadOnlyList<TrayMenuEntry> Menu(TrayFacts facts, StartupState startup, bool roseAppInstalled)
	{
		var serving = facts.NotServing is null;
		var canOpen = serving && roseAppInstalled;
		var why = !roseAppInstalled ? " (not installed)" : !serving ? " (not serving)" : string.Empty;

		var entries = new List<TrayMenuEntry>
		{
			new(TrayAction.OpenRose, "Open RoseMCP" + why, Enabled: canOpen),
			new(TrayAction.DebugAnApp, "Debug an app…" + why, Enabled: canOpen),
		};

		var needs = NeedsYou(facts);

		if (needs.Count > 0)
		{
			entries.Add(TrayMenuEntry.Separator);
			entries.AddRange(needs);
		}

		entries.Add(TrayMenuEntry.Separator);
		entries.Add(new TrayMenuEntry(TrayAction.StartWithWindows, StartupText(startup), Checked: startup.Enabled));
		entries.Add(new TrayMenuEntry(TrayAction.OpenLogFolder, "Open the log folder"));
		entries.Add(new TrayMenuEntry(TrayAction.Exit, "Exit"));

		return entries;
	}

	/// <summary>
	/// The startup item's text. An install that has moved is said rather than shown as off: Windows still
	/// starts the old copy, so "off" would be a lie and "on" would point at the wrong exe. A refusal is said
	/// once, on the next menu, because a toggle that silently did nothing is worse than one not offered.
	/// </summary>
	private static string StartupText(StartupState startup) =>
		startup.Refused ? "Start with Windows (Windows refused)"
		: startup.Elsewhere ? "Start with Windows (registered elsewhere)"
		: "Start with Windows";

	/// <summary>"RoseMCP", the version, and the short commit with whether the tree it was built from was modified.</summary>
	private static string Name(BuildIdentity build)
	{
		var name = $"RoseMCP {build.Version}";

		if (build.ShortCommit() is { } commit) name += build.Dirty == true ? $" ({commit}, modified)" : $" ({commit})";

		return name;
	}

	/// <summary>The first line of <paramref name="text"/>, cut to <paramref name="limit"/> characters with an ellipsis where it was cut.</summary>
	internal static string Excerpt(string text, int limit)
	{
		var line = text.Split('\n', 2)[0].Trim();

		return line.Length <= limit ? line : line[..(limit - 1)].TrimEnd() + "…";
	}
}
