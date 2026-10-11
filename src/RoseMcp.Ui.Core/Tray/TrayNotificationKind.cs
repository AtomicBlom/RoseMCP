namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// The only things the tray interrupts a person for. Anything else is in the tooltip or the menu,
/// where it waits to be looked at, because a notification is only worth its interruption when
/// there is something to act on.
/// </summary>
public enum TrayNotificationKind
{
	/// <summary>A newer release, said once for each version.</summary>
	Release,

	/// <summary>What a restart did to the agents connected across it.</summary>
	Restart,

	/// <summary>Rose failed, with the log that explains it.</summary>
	Failure,

	/// <summary>An agent answered a note the person sent it.</summary>
	NoteAnswered,
}
