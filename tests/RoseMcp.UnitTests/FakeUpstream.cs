using System.Collections.Concurrent;

using ModelContextProtocol.Client;

using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The relay's upstream for <see cref="FakeBroker"/>s: whichever broker is current answers a
/// connect, and none being current is a tray that is down.
/// </summary>
internal sealed class FakeUpstream : RelayUpstream
{
	private readonly ConcurrentDictionary<McpClient, FakeBroker> _owners = new();
	private volatile FakeBroker? _current;
	private volatile FakeBroker? _watching;
	private int _connections;
	private int _watchesToEndEarly;

	public FakeUpstream(FakeBroker? current, bool reconnects = true)
	{
		_current = current;
		Reconnects = reconnects;
	}

	/// <summary>The broker a connect reaches, or null for none listening.</summary>
	public FakeBroker? Current
	{
		get => _current;
		set => _current = value;
	}

	/// <summary>The broker the relay is watching now, set once it has moved its watch to a new connection.</summary>
	public FakeBroker? Watching => _watching;

	/// <summary>How many connections the relay has opened.</summary>
	public int Connections => _connections;

	public override string Name => "The fake broker";

	public override bool Reconnects { get; }

	/// <summary>Makes the next watch end at once, with its broker still there.</summary>
	public void EndNextWatchEarly() => Interlocked.Exchange(ref _watchesToEndEarly, 1);

	public override async Task<McpClient> ConnectAsync(TimeSpan within, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + within;

		while (true)
		{
			if (_current is { } broker)
			{
				var client = await broker.ConnectAsync(cancellationToken);
				_owners[client] = broker;
				Interlocked.Increment(ref _connections);

				return client;
			}

			if (DateTime.UtcNow >= deadline) throw new HttpRequestException("Nothing is listening.");

			await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
		}
	}

	public override Task<RelayHello?> HelloAsync(McpClient client, CancellationToken cancellationToken)
	{
		if (!_owners.TryGetValue(client, out var broker)) return Task.FromResult<RelayHello?>(null);

		return Task.FromResult<RelayHello?>(new RelayHello
		{
			Instance = broker.Instance,
			StartedUtc = DateTimeOffset.UtcNow,
			Build = broker.Build,
			RelayProtocol = broker.RelayProtocol,
			Relays = 1,
		});
	}

	public override async Task WatchAsync(McpClient client, string? instance, CancellationToken cancellationToken)
	{
		_watching = _owners.GetValueOrDefault(client);

		if (Interlocked.Exchange(ref _watchesToEndEarly, 0) == 1) return;

		await client.Completion.WaitAsync(cancellationToken);
	}
}
