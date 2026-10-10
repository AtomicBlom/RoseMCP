namespace RoseMcp.Settings;

/// <summary>
/// What a person has chosen about how RoseMCP behaves, across every host on this machine.
/// <para>
/// Per machine rather than per session or per solution, because these are preferences about the
/// tools rather than facts about a repository -- and because the thing that reads one is not
/// always the thing that set it: the tray writes the inspector's setting and the broker acts on it.
/// </para>
/// </summary>
public sealed record RoseSettings
{
	/// <summary>
	/// Whether attaching a debugger opens the inspector for the target.
	/// <para>
	/// Off by default, which is a judgement about the common case rather than caution. An agent
	/// attaches and detaches many times in a working session, and a window appearing each time is
	/// an interruption nobody asked for. Somebody who wants it turns it on once.
	/// </para>
	/// </summary>
	public bool ShowInspectorOnAttach { get; init; }

	/// <summary>
	/// Whether a workspace whose analyzer, generator or code-fix assembly was rebuilt is reloaded once nobody
	/// has used it for a minute, rather than only told that <c>rose_workspace_reload</c> would pick it up.
	/// <para>
	/// Off by default, because a reload is a design-time build of every project and starts a new worker:
	/// somebody rebuilding an analyzer they are not working on would pay for that unasked. Somebody who is
	/// working on one turns it on once.
	/// </para>
	/// </summary>
	public bool ReloadRebuiltAnalyzersWhenIdle { get; init; }
}
