namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// A notification raised while the tray has no icon to raise it from, held until it has one.
/// <para>
/// Windows shows a balloon only from an icon it has added, and at sign-in it can refuse to add one until the
/// notification area exists. Every notification is said once -- a release claimed, a restart reported, a
/// failure counted -- so one dropped for want of an icon would never be said at all. The newest is kept: it
/// replaces the one before on screen anyway, and the tooltip and menu carry what it replaced. Not
/// thread-safe; the tray calls it on its UI thread only.
/// </para>
/// </summary>
public sealed class PendingNotification
{
	private TrayNotification? _held;

	/// <summary>Whether a notification is waiting for the icon.</summary>
	public bool IsHolding => _held is not null;

	/// <summary>
	/// What to show now: <paramref name="notification"/> when the icon exists, or nothing, holding it in place
	/// of whatever was held, when it does not.
	/// </summary>
	public TrayNotification? Raise(TrayNotification notification, bool iconCreated)
	{
		if (iconCreated) return notification;

		_held = notification;
		return null;
	}

	/// <summary>The notification held for the icon that now exists, once; null when nothing is held.</summary>
	public TrayNotification? IconCreated()
	{
		var held = _held;
		_held = null;

		return held;
	}

	/// <summary>
	/// Whether to claim <paramref name="latest"/> as announced. Not while the icon is missing: a claim is shared
	/// with every tray on the machine and says the person was told, which a balloon nobody could show did not
	/// do. The release is claimed when it can be shown.
	/// </summary>
	/// <param name="available">Whether the update check found a newer release.</param>
	/// <param name="latest">The release it found.</param>
	/// <param name="announcedThisRun">The release this run already announced.</param>
	/// <param name="iconCreated">Whether the icon exists to announce it from.</param>
	public static bool ShouldClaimRelease(bool available, string? latest, string? announcedThisRun, bool iconCreated) =>
		iconCreated
			&& available
			&& latest is { Length: > 0 }
			&& !string.Equals(latest, announcedThisRun, StringComparison.OrdinalIgnoreCase);
}
