namespace RoseMcp.Ui.Core.Tray;

/// <summary>What choosing a tray menu item, or clicking a notification, does.</summary>
public enum TrayAction
{
	/// <summary>Not an action: a line between groups of items.</summary>
	Separator,

	/// <summary>Opens the RoseMCP app, in a process of its own.</summary>
	OpenRose,

	/// <summary>Opens the RoseMCP app to debug an app.</summary>
	DebugAnApp,

	/// <summary>Opens the newer release's page in a browser.</summary>
	OpenRelease,

	/// <summary>Opens the checkout this build was made in, which has moved on past it.</summary>
	OpenCheckout,

	/// <summary>Shows the log that explains the last failure, and stops counting it as waiting.</summary>
	OpenFailureLog,

	/// <summary>Opens the RoseMCP app on the answers to a note, and stops counting them as waiting.</summary>
	OpenNoteAnswers,

	/// <summary>Turns starting with Windows on or off.</summary>
	StartWithWindows,

	/// <summary>Opens the folder every RoseMCP process logs under.</summary>
	OpenLogFolder,

	/// <summary>Stops the broker, which takes every worker with it, and ends the tray.</summary>
	Exit,
}
