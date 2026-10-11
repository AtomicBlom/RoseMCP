namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// What the tray icon shows. Three states and no animation: "working" is not one, because agents are
/// nearly always working, and an icon that is always moving says nothing.
/// </summary>
public enum TrayIconState
{
	/// <summary>The broker is taking calls and nothing is waiting for the person.</summary>
	Serving,

	/// <summary>The broker is taking calls and something is waiting for the person; the tooltip and the menu name what.</summary>
	NeedsYou,

	/// <summary>The broker cannot take calls: it is starting, stopping, or failed to start. The tooltip says which.</summary>
	NotServing,
}
