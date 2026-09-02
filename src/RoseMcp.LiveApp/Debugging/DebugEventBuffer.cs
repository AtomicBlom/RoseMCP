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
	/// Everything captured after <paramref name="after"/>, newest cursor and buffer bounds alongside,
	/// capped at <paramref name="limit"/> so one call cannot return an unbounded page.
	/// </summary>
	public (IReadOnlyList<LiveDebugEvent> Events, long NextCursor, long OldestAvailable, long TotalObserved) ReadAfter(
		long after,
		int limit = 500)
	{
		lock (_gate)
		{
			var oldest = _events.Count == 0 ? 0 : _events.Peek().Sequence;

			var page = _events
				.Where(entry => entry.Sequence > after)
				.Take(limit)
				.ToList();

			var nextCursor = page.Count == 0 ? Math.Max(after, _observed) : page[^1].Sequence;
			return (page, nextCursor, oldest, _observed);
		}
	}
}
