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
		}
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
