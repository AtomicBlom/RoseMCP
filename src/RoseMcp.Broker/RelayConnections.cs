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
/// old one is still being torn down is one relay rather than two.
/// </para>
/// </summary>
public sealed class RelayConnections
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, int> _watches = new(StringComparer.Ordinal);

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
	/// Counts <paramref name="relayId"/> as connected until the returned scope is disposed, which the
	/// watch route does when its request ends for any reason.
	/// </summary>
	/// <exception cref="ArgumentException">The id is not in the shape a relay mints.</exception>
	public IDisposable Watch(string relayId)
	{
		if (!RelayProtocol.IsRelayId(relayId))
		{
			throw new ArgumentException(
				$"A relay names itself in the {RelayProtocol.RelayHeader} header with thirty-two lowercase hex digits.",
				nameof(relayId));
		}

		lock (_gate)
		{
			_watches[relayId] = _watches.GetValueOrDefault(relayId) + 1;
		}

		return new Watching(this, relayId);
	}

	/// <summary>What a relay is told about this broker.</summary>
	public RelayHello Hello(BuildIdentity build) => new()
	{
		Instance = Instance,
		StartedUtc = StartedUtc,
		Build = build,
		RelayProtocol = RelayProtocol.Version,
		Relays = Count,
	};

	private void Release(string relayId)
	{
		lock (_gate)
		{
			if (!_watches.TryGetValue(relayId, out var open)) return;

			if (open <= 1) _watches.Remove(relayId);
			else _watches[relayId] = open - 1;
		}
	}

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
