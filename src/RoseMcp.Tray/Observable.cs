using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RoseMcp.Tray;

/// <summary>
/// The smallest thing that lets a row be updated in place rather than replaced.
/// <para>
/// The window refreshes several times a second while work is in flight. Rebuilding the list each
/// time would restart every progress bar animation, drop the scroll position, and collapse any
/// expander the reader had just opened. Updating the existing rows pushes only what changed.
/// </para>
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Assigns and notifies, but only when the value actually moved.</summary>
	protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return;

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
	}
}
