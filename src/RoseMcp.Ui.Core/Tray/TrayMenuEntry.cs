namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// One item of the tray's menu, as the icon is to show it: what it does, what it says, whether it is
/// ticked, and whether it can be chosen.
/// </summary>
/// <param name="Action">What choosing it does; <see cref="TrayAction.Separator"/> for a line.</param>
/// <param name="Text">What it says. Empty for a separator.</param>
/// <param name="Checked">Ticked or not, for an item that toggles; null for one that does not.</param>
/// <param name="Enabled">Whether it can be chosen. An item that could not work says why in its text.</param>
public sealed record TrayMenuEntry(TrayAction Action, string Text, bool? Checked = null, bool Enabled = true)
{
	/// <summary>A line between groups of items.</summary>
	public static readonly TrayMenuEntry Separator = new(TrayAction.Separator, string.Empty);
}
