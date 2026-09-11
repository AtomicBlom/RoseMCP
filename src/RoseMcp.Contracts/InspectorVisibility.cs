namespace RoseMcp.Contracts;

/// <summary>
/// Whether starting a debug session should open the inspector on it.
/// <para>
/// Three values rather than a bool, because the useful default is neither yes nor no: it is
/// "whatever the person at this machine chose". An agent has no way to know whether somebody is
/// sitting in front of the screen wanting a window, and a bool would force it to guess on their
/// behalf every time.
/// </para>
/// </summary>
public enum InspectorVisibility
{
	/// <summary>
	/// What the person chose, from the tray or the inspector. The default, and the value an agent
	/// should almost always leave alone.
	/// </summary>
	UserPreference,

	/// <summary>Open it, whatever the preference says. For a caller acting on somebody's request.</summary>
	Always,

	/// <summary>Do not, whatever the preference says. For work nobody is watching.</summary>
	Never,
}
