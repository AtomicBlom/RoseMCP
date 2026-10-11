namespace RoseMcp.Broker;

/// <summary>
/// Runs a refresh in the background when asked, one at a time, folding a burst of requests into as few
/// runs as keep the last request answered: a request that arrives while a run is in flight is never
/// dropped, it makes one more run follow.
/// <para>
/// The last request is the one that matters. The broker asks a worker about itself after every call, and
/// a call whose read found a rebuilt analyzer may end while a refresh begun for an earlier call is still
/// in flight -- one that may already have been answered before that read ran. Dropped, the rebuild never
/// reaches the broker, and the idle reload a person turned on never happens.
/// </para>
/// </summary>
/// <param name="refresh">The work, which handles its own failures; one that throws still lets the next request run.</param>
/// <param name="shouldRun">Whether to run at all, checked before each run, so a stopped owner is not refreshed.</param>
public sealed class CoalescedRefresh(Func<Task> refresh, Func<bool> shouldRun)
{
	private int _running;
	private int _wanted;

	/// <summary>Whether a run is in flight or about to start.</summary>
	public bool Running => Volatile.Read(ref _running) == 1;

	/// <summary>
	/// Asks for a run that starts after this call. Starts one where none is running, and otherwise leaves it
	/// to the one running, which looks for this request before it stops.
	/// </summary>
	public void Request()
	{
		if (!shouldRun()) return;

		// The store is ordered before the load of _running by the compare-exchange, which is a full fence: the
		// runner reads _wanted after releasing _running, so one side always sees the other.
		Volatile.Write(ref _wanted, 1);
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

		_ = Detached.Run(RunAsync);
	}

	private async Task RunAsync()
	{
		try
		{
			while (shouldRun() && Interlocked.Exchange(ref _wanted, 0) == 1)
			{
				await refresh();
			}
		}
		finally
		{
			// An exchange, not a volatile write: a volatile store followed by a volatile load may be reordered,
			// so this could read _wanted as clear while a requester, having set it, still read _running as set
			// -- and each would leave the run to the other. The exchange is a full fence, which is what makes
			// "release, then look once more" and "ask, then look" impossible to interleave that way.
			Interlocked.Exchange(ref _running, 0);

			if (Volatile.Read(ref _wanted) == 1) Request();
		}
	}
}
