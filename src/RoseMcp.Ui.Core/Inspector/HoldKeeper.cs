using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Takes a hold on the stop a target is sitting at, or gives one back. The session is already
/// decided by the time a keeper exists, so this is the whole of what one needs from the host.
/// </summary>
public delegate Task<LiveHoldResult> HoldCall(int? seconds, bool release, CancellationToken cancellationToken);

/// <summary>
/// The one hold on a target, shared by every pane reading the stop it is sitting at.
/// <para>
/// A hold is per session, not per reader: the host has one, and the last request about it wins. So
/// two panes each taking and releasing their own is not two holds, it is a race -- the stack pane
/// released on being hidden would give away the hold the threads pane had just taken, and the
/// target would resume under a reader who had done nothing but change tab.
/// </para>
/// <para>
/// Readers say what they want rather than what to do, and the keeper reconciles. That makes
/// <see cref="Want"/> idempotent, which matters because it is called from a poll: a pane that says
/// the same thing every second must not produce a request every second, and a pane that forgets to
/// let go is corrected by its own next poll rather than leaving somebody's application stopped.
/// </para>
/// <para>
/// Single-threaded, on whichever thread the window runs on. Everything that drives it -- the poll,
/// the tick, a tab changing -- is already there, and the in-flight request is what keeps two callers
/// from asking at once rather than a lock.
/// </para>
/// </summary>
public sealed class HoldKeeper
{
	/// <summary>
	/// How long a hold is taken for. Long enough that reading a stack and opening a few values never
	/// runs out, short enough that a reader who walks away does not leave somebody's application
	/// frozen for the host's whole cap.
	/// </summary>
	public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(120);

	private readonly HoldCall _ask;
	private readonly Action<Exception>? _report;

	// By reference, because what identifies a reader is that it is that pane. Two panes are never
	// equal in any sense the keeper cares about, and a row-like Equals would silently merge them.
	private readonly HashSet<object> _readers = new(ReferenceEqualityComparer.Instance);

	private Task _settling = Task.CompletedTask;
	private bool _held;
	private bool _renew;
	private bool _refused;
	private long _stopSequence;
	private DateTime _deadlineUtc;

	public HoldKeeper(HoldCall ask, Action<Exception>? report = null)
	{
		_ask = ask;
		_report = report;
	}

	/// <summary>Whether the target is being kept still for a reader right now.</summary>
	public bool Held => _held;

	/// <summary>
	/// Why the target is not being held, when somebody wanted it to be and it is not. Empty
	/// otherwise, including while nobody wants one.
	/// </summary>
	public string Detail { get; private set; } = string.Empty;

	/// <summary>
	/// Says whether one reader still needs the target to stay where it is. Safe to call on every
	/// poll with the same answer: only a change asks the host anything.
	/// </summary>
	/// <returns>
	/// When the keeper has finished asking, so a reader can hold the target still before it reads
	/// rather than while it reads. A stack read under the safety timer can be answered and stale
	/// before the first frame is on screen.
	/// </returns>
	public Task Want(object reader, bool wanted)
	{
		var moved = wanted ? _readers.Add(reader) : _readers.Remove(reader);

		return moved ? Settle() : _settling;
	}

	/// <summary>
	/// Notices where the target is stopped, which is what makes a hold worth re-taking.
	/// <para>
	/// A hold belongs to the stop it was taken at: continuing clears it in the host, and the stop the
	/// target lands in next is on its own safety timer. So a sequence that has moved means nothing is
	/// held here, whatever was held a moment ago -- and a refusal earned at the last stop is not this
	/// stop's answer either.
	/// </para>
	/// </summary>
	/// <returns>When the keeper has finished asking, the same as <see cref="Want"/>.</returns>
	public Task AtStop(long stopSequence)
	{
		if (stopSequence == _stopSequence) return _settling;

		_stopSequence = stopSequence;
		_held = false;
		_refused = false;
		Detail = string.Empty;

		return Settle();
	}

	/// <summary>
	/// Renews a hold that is running out, which is what lets a reader take as long as they like
	/// without the cap being raised for everybody who walks away.
	/// <para>
	/// Asked on a clock rather than scheduled for the deadline, so it survives a renewal that failed,
	/// a machine that slept, and a stop replaced by a newer one.
	/// </para>
	/// </summary>
	/// <returns>When the keeper has finished asking, the same as <see cref="Want"/>.</returns>
	public Task Tick(DateTime utcNow)
	{
		if (!_held || _readers.Count == 0) return _settling;
		if (!HoldCountdown.ShouldRenew(_deadlineUtc, utcNow)) return _settling;

		_renew = true;
		return Settle();
	}

	/// <summary>
	/// Gives the hold back, whatever this keeper believes it holds.
	/// <para>
	/// Unconditional because what it is for is a window closing, and a hold the keeper has lost track
	/// of is exactly the one that leaves somebody's application stopped. Releasing a hold that is not
	/// there is answered rather than refused, so asking costs nothing. Nothing is reported: the
	/// window asking has already begun to close and has nowhere to put it.
	/// </para>
	/// </summary>
	public async Task ReleaseAsync()
	{
		_readers.Clear();
		_held = false;
		_renew = false;
		Detail = string.Empty;

		try
		{
			await _ask(null, true, CancellationToken.None);
		}
		catch
		{
			// A hold that outlives this is bounded by the host's own cap, so the worst of it is a
			// target that frees itself a couple of minutes later.
		}
	}

	/// <summary>
	/// Brings the host's hold into line with what the readers want, one request at a time.
	/// <para>
	/// A loop rather than a request per caller, because a reader arriving while a release is in flight
	/// would otherwise be answered by that release. Re-reading what is wanted after every round trip
	/// gives the last word to the last caller instead of to whichever request finished last.
	/// </para>
	/// </summary>
	private Task Settle() => _settling.IsCompleted ? _settling = SettleAsync() : _settling;

	private async Task SettleAsync()
	{
		// After the caller's own block, never inside it. Changing tab tells the arriving pane and the
		// leaving one in one pass, and reconciling on the first of those would release the hold and take
		// it again -- leaving the target free in between, long enough for its own safety timer to run the
		// reader off the stop they were reading. A yield collapses a batch of readers into one decision
		// whatever order they arrive in, which is a property no call site then has to remember.
		await Task.Yield();

		while (true)
		{
			var wanted = _readers.Count > 0;

			if (wanted && !_held && !_refused)
			{
				await TakeAsync();
				continue;
			}

			if (wanted && _held && _renew)
			{
				_renew = false;
				await TakeAsync();
				continue;
			}

			if (!wanted && _held)
			{
				await GiveAsync();
				continue;
			}

			_renew = false;
			return;
		}
	}

	private async Task TakeAsync()
	{
		try
		{
			var taken = await _ask((int)HoldFor.TotalSeconds, false, CancellationToken.None);

			_held = taken.Applied;
			_refused = !taken.Applied;
			_deadlineUtc = taken.Stop?.ResumeDeadlineUtc ?? DateTime.UtcNow + HoldFor;
			Detail = taken.Applied
				? string.Empty
				: taken.Detail ?? "The target could not be held still.";
		}
		catch (Exception exception)
		{
			// A failure counts as this stop's answer. Asking again every second would be a window
			// hammering a tray that cannot answer, and the next stop asks afresh anyway.
			_held = false;
			_refused = true;
			Detail = exception.Message;
			_report?.Invoke(exception);
		}
	}

	private async Task GiveAsync()
	{
		try
		{
			await _ask(null, true, CancellationToken.None);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			// Believed released either way. A release that failed is bounded by the host's own cap,
			// while a keeper that went on believing it held one would refuse the next reader's take.
			_held = false;
		}
	}
}
