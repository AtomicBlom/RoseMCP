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
}
