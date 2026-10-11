namespace RoseMcp.Contracts;

/// <summary>
/// What a broker tells a relay about itself, at <see cref="RelayProtocol.HelloRoute"/> and as the
/// first line of <see cref="RelayProtocol.WatchRoute"/>.
/// <para>
/// The instance is what lets a relay tell a broker that restarted from one it merely reconnected
/// to. A build cannot: a tray restarted without a deploy is the same build, and its debug sessions
/// and loaded solutions are gone all the same.
/// </para>
/// </summary>
public sealed record RelayHello
{
	/// <summary>An id the broker minted when it started, different every time it starts.</summary>
	public required string Instance { get; init; }

	/// <summary>When this broker started.</summary>
	public required DateTimeOffset StartedUtc { get; init; }

	/// <summary>The broker's build.</summary>
	public required BuildIdentity Build { get; init; }

	/// <summary>The <see cref="RelayProtocol.Version"/> the broker was built with.</summary>
	public required int RelayProtocol { get; init; }

	/// <summary>
	/// How many relays are watching this broker now, each counted once however many watches it
	/// holds. After a deploy this is how many sessions found their way back.
	/// </summary>
	public required int Relays { get; init; }

	/// <summary>
	/// The process ids of the relays watching, in ascending order, from each relay that sent one. A
	/// deploy counts only the ids of the relays it kept running, since every relay on the port is in
	/// <see cref="Relays"/> -- one from a build output or another install included.
	/// </summary>
	public IReadOnlyList<int> RelayProcesses { get; init; } = [];
}
