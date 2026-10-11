namespace RoseMcp.Server;

/// <summary>
/// The restart notice waiting for a result to carry it, handed to exactly one.
/// <para>
/// Calls run concurrently, and several are usually in flight when a broker comes back: each that
/// asks gets the notice or nothing, never both, so the line is said once rather than on every
/// result that happened to finish first. A reconnect while one is waiting folds into it rather than
/// replacing it, so the sessions that ended in the first restart are not lost to the second.
/// </para>
/// </summary>
public sealed class PendingNotice
{
	private RestartNotice? _pending;

	/// <summary>Leaves <paramref name="notice"/> for the next result, folded into any already waiting.</summary>
	public void Add(RestartNotice notice)
	{
		while (true)
		{
			var waiting = Volatile.Read(ref _pending);
			var merged = waiting is null ? notice : waiting.Then(notice);

			// Retried rather than locked: a result claiming the waiting notice between the read and
			// the write must not have the folded copy say the same thing again on the next result.
			if (ReferenceEquals(Interlocked.CompareExchange(ref _pending, merged, waiting), waiting)) return;
		}
	}

	/// <summary>The waiting notice, or null; whoever gets it is the only one who does.</summary>
	public RestartNotice? Claim() => Interlocked.Exchange(ref _pending, null);
}
