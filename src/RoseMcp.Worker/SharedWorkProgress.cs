namespace RoseMcp.Worker;

/// <summary>
/// Work that every caller waiting on this workspace is waiting on: the initial load, a restore, a
/// reload after a branch switch.
/// <para>
/// None of it has a request of its own to report against. Loading starts when the process does,
/// before any client has asked for anything, and a reload happens inside whichever read barrier
/// noticed the change. So reports fan out to every call currently in flight instead. That is not a
/// fudge: a call blocked behind a reload really is waiting for that reload, and saying so is the
/// only honest answer to "why is this taking so long".
/// </para>
/// </summary>
public sealed class SharedWorkProgress : IWorkProgress
{
	private readonly object _gate = new();
	private readonly List<IWorkProgress> _listeners = [];

	public void Report(string message, double? percentComplete = null)
	{
		IWorkProgress[] listeners;
		lock (_gate)
		{
			if (_listeners.Count == 0) return;

			listeners = [.. _listeners];
		}

		// Outside the lock: a listener writes to a transport, and holding a lock across that would
		// let a slow client stall the load it is watching.
		foreach (var listener in listeners)
		{
			listener.Report(message, percentComplete);
		}
	}

	/// <summary>
	/// Passes shared-work reports to <paramref name="listener"/> until the returned handle is
	/// disposed. A null listener is a caller with nobody to tell, and costs nothing.
	/// </summary>
	public IDisposable Follow(IWorkProgress? listener)
	{
		if (listener is null) return NotFollowing.Instance;

		lock (_gate)
		{
			_listeners.Add(listener);
		}

		return new Subscription(this, listener);
	}

	private void Unfollow(IWorkProgress listener)
	{
		lock (_gate)
		{
			_listeners.Remove(listener);
		}
	}

	private sealed class Subscription(SharedWorkProgress shared, IWorkProgress listener) : IDisposable
	{
		public void Dispose() => shared.Unfollow(listener);
	}

	private sealed class NotFollowing : IDisposable
	{
		public static readonly NotFollowing Instance = new();

		public void Dispose()
		{
		}
	}
}
