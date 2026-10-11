using System.Globalization;
using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Which relays are connected to this broker, and which broker this is.
/// <para>
/// A relay counts as connected while it holds <see cref="RelayProtocol.WatchRoute"/> open, which
/// it does for as long as it is relaying here. That is a better count than any number of requests
/// seen, because it goes down when a relay goes: after a deploy it says exactly how many sessions
/// found their way back to the new broker, which is what the deploy reports.
/// </para>
/// <para>
/// Counted by the relay's own id rather than by watch, so a relay re-opening its watch while the
/// old one is still being torn down is one relay rather than two. Each relay's process id is kept
/// beside it, because the count is every relay on the port and a deploy wants only its own: it
/// matches these ids against the relay processes running from the install it replaced.
/// </para>
/// </summary>
public sealed class RelayConnections
{
	/// <summary>How long a relay's stop is remembered: long past any grace a reader gives a relay re-opening its watch.</summary>
	private static readonly TimeSpan StoppedMemory = TimeSpan.FromHours(1);

	/// <summary>The most stops remembered at once, the oldest forgotten first, since every relay id is a caller's choice.</summary>
	private const int MaxStopped = 1024;

	private readonly Lock _gate = new();
	private readonly Dictionary<string, Watcher> _watches = new(StringComparer.Ordinal);

	/// <summary>When each relay that holds no watch now last let go of one. Guarded by <see cref="_gate"/>.</summary>
	private readonly Dictionary<string, DateTimeOffset> _stopped = new(StringComparer.Ordinal);

	private readonly TimeProvider _time;

	/// <summary>A registry reading the time from <paramref name="time"/>, or the system's where none is given.</summary>
	public RelayConnections(TimeProvider? time = null)
	{
		_time = time ?? TimeProvider.System;
	}

	/// <summary>Minted once per broker start, which is what a relay compares to tell a restart from a reconnect.</summary>
	public string Instance { get; } = Guid.NewGuid().ToString("N");

	/// <summary>
	/// When this broker started: when the registry was made, which <see cref="AdminApi.MapRoseAdminApi"/>
	/// does as the host maps its routes, just before it begins serving.
	/// </summary>
	public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

	/// <summary>How many distinct relays are watching now.</summary>
	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _watches.Count;
			}
		}
	}

	/// <summary>
	/// The process id in a <see cref="RelayProtocol.ProcessHeader"/>, or null where there is none or it
	/// is not a positive decimal number. A relay without one is still counted, just not as any process.
	/// </summary>
	public static int? ParseProcessId(string? value)
	{
		var parsed = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId);

		return parsed && processId > 0 ? processId : null;
	}

	/// <summary>
	/// Counts <paramref name="relayId"/> as connected until the returned scope is disposed, which the
	/// watch route does when its request ends for any reason.
	/// </summary>
	/// <param name="relayId">The id the relay minted, the same on every watch it opens.</param>
	/// <param name="processId">The relay's process, where it said; the latest watch's word stands.</param>
	/// <exception cref="ArgumentException">The id is not in the shape a relay mints.</exception>
	public IDisposable Watch(string relayId, int? processId = null)
	{
		if (!RelayProtocol.IsRelayId(relayId))
		{
			throw new ArgumentException(
				$"A relay names itself in the {RelayProtocol.RelayHeader} header with thirty-two lowercase hex digits.",
				nameof(relayId));
		}

		lock (_gate)
		{
			var open = _watches.TryGetValue(relayId, out var watcher) ? watcher.Open : 0;
			_watches[relayId] = new Watcher(open + 1, processId ?? watcher?.ProcessId);
			_stopped.Remove(relayId);
		}

		return new Watching(this, relayId);
	}

	/// <summary>What a relay is told about this broker.</summary>
	public RelayHello Hello(BuildIdentity build)
	{
		lock (_gate)
		{
			return new RelayHello
			{
				Instance = Instance,
				StartedUtc = StartedUtc,
				Build = build,
				RelayProtocol = RelayProtocol.Version,
				Relays = _watches.Count,
				RelayProcesses = [.. _watches.Values.Select(watcher => watcher.ProcessId).OfType<int>().Distinct().Order()],
			};
		}
	}

	/// <summary>
	/// The relays watching now, each with the process it said it is, and when each relay that has stopped
	/// last let go of its watch: one copy, taken under one lock, so a caller holding a lock of its own reads
	/// both without taking this one inside that, and never sees a relay as neither watching nor stopped.
	/// </summary>
	public RelayWatchers Watchers()
	{
		lock (_gate)
		{
			return new RelayWatchers(
				_watches.ToDictionary(pair => pair.Key, pair => pair.Value.ProcessId, StringComparer.Ordinal),
				new Dictionary<string, DateTimeOffset>(_stopped, StringComparer.Ordinal));
		}
	}

	private void Release(string relayId)
	{
		var now = _time.GetUtcNow();

		lock (_gate)
		{
			if (!_watches.TryGetValue(relayId, out var watcher)) return;

			if (watcher.Open > 1)
			{
				_watches[relayId] = watcher with { Open = watcher.Open - 1 };
				return;
			}

			_watches.Remove(relayId);
			_stopped[relayId] = now;

			// Only the latest stops matter, to say a relay went a moment ago rather than long since; a relay
			// that has been gone this long is gone by any reading, so its entry is no loss.
			var forgotten = _stopped
				.Where(pair => now - pair.Value > StoppedMemory)
				.Select(pair => pair.Key)
				.ToList();

			foreach (var id in forgotten) _stopped.Remove(id);

			if (_stopped.Count > MaxStopped)
			{
				foreach (var id in _stopped.OrderBy(pair => pair.Value).Take(_stopped.Count - MaxStopped).Select(pair => pair.Key).ToList())
				{
					_stopped.Remove(id);
				}
			}
		}
	}

	/// <summary>One relay's open watches, and the process it said it is.</summary>
	private sealed record Watcher(int Open, int? ProcessId);

	/// <summary>One open watch. Disposing it twice releases it once.</summary>
	private sealed class Watching(RelayConnections owner, string relayId) : IDisposable
	{
		private int _released;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(relayId);
		}
	}
}
