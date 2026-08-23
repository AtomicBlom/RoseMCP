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

	private Phase? _current;

	public void Report(string message, double? percentComplete = null)
	{
		IWorkProgress[] listeners;
		lock (_gate)
		{
			_current = new Phase(message, percentComplete);

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
	/// Marks a shared operation as under way until the returned handle is disposed.
	/// <para>
	/// This is what makes a load visible from its first second. The client that will carry the
	/// reports has not connected yet when loading starts, so early reports would otherwise go
	/// nowhere; and a tool call arriving halfway through a load would show nothing until the next
	/// project happened to finish, which on a slow project is a long time to look idle.
	/// </para>
	/// </summary>
	public IDisposable Begin(string message)
	{
		Report(message);

		return new Operation(this);
	}

	/// <summary>
	/// Passes shared-work reports to <paramref name="listener"/> until the returned handle is
	/// disposed, starting with whatever is going on right now. A null listener is a caller with
	/// nobody to tell, and costs nothing.
	/// </summary>
	public IDisposable Follow(IWorkProgress? listener)
	{
		if (listener is null) return NotFollowing.Instance;

		Phase? current;
		lock (_gate)
		{
			_listeners.Add(listener);
			current = _current;
		}

		if (current is { } phase) listener.Report(phase.Message, phase.PercentComplete);

		return new Subscription(this, listener);
	}

	private void Unfollow(IWorkProgress listener)
	{
		lock (_gate)
		{
			_listeners.Remove(listener);
		}
	}

	/// <summary>
	/// Nothing shared is happening any more, so there is nothing to catch a newcomer up on. Without
	/// this a call arriving an hour later would be told about the load it missed.
	/// </summary>
	private void End()
	{
		lock (_gate)
		{
			_current = null;
		}
	}

	private sealed record Phase(string Message, double? PercentComplete);

	private sealed class Operation(SharedWorkProgress shared) : IDisposable
	{
		public void Dispose() => shared.End();
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
