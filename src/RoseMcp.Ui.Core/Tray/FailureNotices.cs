namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// Which failures the tray interrupts the person for: each distinct one once, and no more than one a
/// minute, so a handler failing on every poll is one notification and not a stream of them.
/// <para>
/// A failure that is not announced is still not lost: the tray keeps the latest one waiting in its
/// tooltip and menu until the person opens the log, and the log has every one. Not thread-safe; the
/// tray calls it on its UI thread only.
/// </para>
/// </summary>
public sealed class FailureNotices
{
	/// <summary>The least time between two announced failures.</summary>
	public static readonly TimeSpan Spacing = TimeSpan.FromMinutes(1);

	/// <summary>
	/// How many distinct failures are remembered before the memory starts again. A run that has failed in
	/// this many ways has a problem the log says more about than a repeat notification would.
	/// </summary>
	public const int Capacity = 256;

	private readonly HashSet<string> _said = new(StringComparer.Ordinal);
	private DateTimeOffset? _lastSaid;

	/// <summary>
	/// Whether to announce <paramref name="message"/> now. A failure not announced because another came
	/// within <see cref="Spacing"/> is not announced later either: it is counted as said, since the one
	/// that was announced already sent the person to the log that holds both.
	/// </summary>
	public bool Admit(string message, DateTimeOffset now)
	{
		if (_said.Count >= Capacity) _said.Clear();

		var first = _said.Add(TrayStatus.Excerpt(message, 200));
		if (!first) return false;

		var tooSoon = _lastSaid is { } last && now - last < Spacing;
		if (tooSoon) return false;

		_lastSaid = now;
		return true;
	}
}
