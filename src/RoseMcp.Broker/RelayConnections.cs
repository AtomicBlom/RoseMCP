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
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Watcher> _watches = new(StringComparer.Ordinal);

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

	private void Release(string relayId)
	{
		lock (_gate)
		{
			if (!_watches.TryGetValue(relayId, out var watcher)) return;

			if (watcher.Open <= 1) _watches.Remove(relayId);
			else _watches[relayId] = watcher with { Open = watcher.Open - 1 };
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
