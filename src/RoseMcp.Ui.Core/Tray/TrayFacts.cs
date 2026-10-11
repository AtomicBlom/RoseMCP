using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// Everything the tray icon says anything about, read at one moment: whether the broker can take
/// calls, how much it is serving, and what is waiting for the person.
/// <para>
/// A plain record of facts the tray gathers, so that the icon's state, its tooltip and its menu are
/// each a function of one value and can be tested without an icon.
/// </para>
/// </summary>
public sealed record TrayFacts
{
	/// <summary>The build this tray is, which the tooltip names first.</summary>
	public required BuildIdentity Build { get; init; }

	/// <summary>
	/// Why the broker cannot take calls -- starting, stopping, or a start that failed -- or null when
	/// it is serving.
	/// </summary>
	public string? NotServing { get; init; }

	/// <summary>Agent sessions connected now: see <see cref="TrayStatus.AgentsIn"/>.</summary>
	public int Agents { get; init; }

	/// <summary>Workspaces whose worker is alive. A stopped row holds no memory and is not counted.</summary>
	public int Workspaces { get; init; }

	/// <summary>Apps being debugged: one per live-app session.</summary>
	public int DebuggedApps { get; init; }

	/// <summary>The newer release the update check found, or null when there is none.</summary>
	public string? UpdateVersion { get; init; }

	/// <summary>
	/// How many commits the checkout this build was made in has past it, or null where that is not
	/// known or the build names no checkout. Zero means the build is the checkout's <c>HEAD</c>.
	/// </summary>
	public int? CheckoutAhead { get; init; }

	/// <summary>The last failure the person has not yet looked at, or null when there is none.</summary>
	public string? Failure { get; init; }

	/// <summary>How many answers to a note have arrived that the person has not yet opened.</summary>
	public int NotesAnswered { get; init; }
}
