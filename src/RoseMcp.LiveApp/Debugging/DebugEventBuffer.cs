using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// A bounded, thread-safe ring of debug events. ICorDebug callbacks arrive on mscordbi's own thread,
/// often in bursts, while the agent reads only between its turns, so events are captured here and
/// handed out on demand rather than pushed synchronously.
/// <para>
/// Every event gets a monotonic sequence in arrival order, so a reader can ask for only what is new.
/// The ring is bounded, so a reader that falls far behind loses the oldest events; the sequence makes
/// that loss visible rather than silent -- the oldest kept sequence jumps past the reader's cursor.
/// </para>
/// </summary>
public sealed class DebugEventBuffer(int capacity = 4096)
{
	private readonly Lock _gate = new();
	private readonly Queue<LiveDebugEvent> _events = new();
	private long _observed;

	// Readers parked in WaitForAsync, each with what it is waiting for. A list rather than one event
	// because two callers can legitimately be waiting on different kinds at once -- one on a
	// breakpoint hit, another on anything at all.
	private readonly List<Waiter> _waiters = [];

	private sealed record Waiter(
		long After,
		IReadOnlyCollection<LiveDebugEventKind>? Kinds,
		TaskCompletionSource Arrived);

	/// <summary>Records an event, assigning its sequence and timestamp, and dropping the oldest if full.</summary>
	public void Append(
		LiveDebugEventKind kind,
		string message,
		int? threadId = null,
		string? moduleName = null,
		string? exceptionType = null,
		IReadOnlyList<string>? frames = null,
		IReadOnlyList<LiveVariable>? variables = null)
	{
		lock (_gate)
		{
			var sequence = ++_observed;
			_events.Enqueue(new LiveDebugEvent
			{
				Sequence = sequence,
				TimestampUtc = DateTime.UtcNow,
				Kind = kind,
				Message = message,
				ThreadId = threadId,
				ModuleName = moduleName,
				ExceptionType = exceptionType,
				Frames = frames,
				Variables = variables,
			});

			while (_events.Count > capacity)
			{
				_events.Dequeue();
			}

			WakeWaitersFor(kind, sequence);
		}
	}

	/// <summary>
	/// Releases every reader this event answers. Called with the gate held, and it only completes the
	/// tasks -- the waiter re-reads for itself afterwards, so the page it gets is built the same way a
	/// polled one is rather than by a second path that could differ.
	/// </summary>
	private void WakeWaitersFor(LiveDebugEventKind kind, long sequence)
	{
		for (var index = _waiters.Count - 1; index >= 0; index--)
		{
			var waiter = _waiters[index];
			if (sequence <= waiter.After) continue;
			if (waiter.Kinds is { Count: > 0 } && !waiter.Kinds.Contains(kind)) continue;

			_waiters.RemoveAt(index);

			// Asynchronously, because this runs on mscordbi's callback thread with the debuggee
			// stopped. Completing a continuation inline here would run a reader's code on that
			// thread, and nothing in it may block: the callback has to return before the debuggee
			// moves again.
			waiter.Arrived.TrySetResult();
		}
	}

	/// <summary>
	/// Waits until an event past <paramref name="after"/> matching <paramref name="kinds"/> exists, or
	/// until <paramref name="timeout"/>. Returns true if one arrived.
	/// <para>
	/// This is what "notified without polling" means for a turn-based agent (#8). A client that is not
	/// listening between its turns cannot receive a pushed notification, so the useful shape is one
	/// call that does not return until there is something to say. With the kind filter it is also
	/// "wait for the next stop" -- waiting on <see cref="LiveDebugEventKind.BreakpointHit"/> is exactly
	/// that, so the two do not need separate mechanisms.
	/// </para>
	/// <para>
	/// It matters that this returns the moment the event is buffered rather than on a tick: a stopping
	/// breakpoint auto-continues after its safety timeout, so an agent that learns of the stop late
	/// has less of that window left to evaluate anything in, and one that learns of it on a poll
	/// boundary can miss the window entirely.
	/// </para>
	/// </summary>
	public async Task<bool> WaitForAsync(
		long after,
		IReadOnlyCollection<LiveDebugEventKind>? kinds,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		Waiter waiter;
		lock (_gate)
		{
			// Already there, so do not wait at all. Checked under the same gate that Append takes, or
			// an event landing between the check and the registration would be missed and the caller
			// would wait out the whole timeout for something that had already happened.
			if (HasMatch(after, kinds)) return true;

			waiter = new Waiter(after, kinds, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
			_waiters.Add(waiter);
		}

		try
		{
			using var deadline = new CancellationTokenSource(timeout);
			using var either = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
			using var registration = either.Token.Register(() => waiter.Arrived.TrySetResult());

			await waiter.Arrived.Task;
			cancellationToken.ThrowIfCancellationRequested();

			// Asked of the buffer, not of the deadline token. An event arriving at the same moment the
			// timeout fires would otherwise be reported as "nothing arrived", and the caller would be
			// told a stop had not happened when it had -- which is the one answer here that is worse
			// than a slow one.
			lock (_gate)
			{
				return HasMatch(after, kinds);
			}
		}
		finally
		{
			lock (_gate)
			{
				_waiters.Remove(waiter);
			}
		}
	}

	/// <summary>Whether anything past a cursor matches. Callers hold <see cref="_gate"/>.</summary>
	private bool HasMatch(long after, IReadOnlyCollection<LiveDebugEventKind>? kinds)
	{
		foreach (var entry in _events)
		{
			if (entry.Sequence <= after) continue;
			if (kinds is { Count: > 0 } && !kinds.Contains(entry.Kind)) continue;

			return true;
		}

		return false;
	}

	/// <summary>
	/// A page of events after a cursor, optionally of only certain kinds.
	/// <para>
	/// The kind filter is applied here rather than by the caller, and the cursor still advances over
	/// the events it skips. Filtering after the fact would either lose the skipped events' place --
	/// leaving a cursor that re-reads them forever -- or force a caller wanting only exceptions to
	/// pull every module load across the wire to find them. A minute-old app produced 312 events and
	/// 93KB, which is over a client's token cap, so "read it all and grep" is not a usable answer.
	/// </para>
	/// </summary>
	public (IReadOnlyList<LiveDebugEvent> Events, long NextCursor, long OldestAvailable, long TotalObserved, int Skipped) ReadAfter(
		long after,
		int limit = 500,
		IReadOnlyCollection<LiveDebugEventKind>? kinds = null)
	{
		lock (_gate)
		{
			var oldest = _events.Count == 0 ? 0 : _events.Peek().Sequence;

			var matching = new List<LiveDebugEvent>();
			var skipped = 0;
			var lastSeen = after;

			foreach (var entry in _events)
			{
				if (entry.Sequence <= after) continue;
				if (matching.Count >= limit) break;

				lastSeen = entry.Sequence;
				if (kinds is { Count: > 0 } && !kinds.Contains(entry.Kind))
				{
					skipped++;
					continue;
				}

				matching.Add(entry);
			}

			// The cursor is where reading got to, not where the last *match* was, so a filtered read
			// does not hand back a cursor that re-delivers everything it just chose to skip.
			var nextCursor = _events.Count == 0 ? Math.Max(after, _observed) : lastSeen;
			return (matching, nextCursor, oldest, _observed, skipped);
		}
	}
}
