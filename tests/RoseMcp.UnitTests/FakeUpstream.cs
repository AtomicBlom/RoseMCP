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
	private readonly ConcurrentDictionary<McpClient, int> _numbers = new();
	private volatile FakeBroker? _current;
	private volatile FakeBroker? _watching;
	private volatile TaskCompletionSource? _closedWatchesHeld;
	private volatile TaskCompletionSource? _silentHello;
	private int _silentHelloOn;
	private bool _helloAnswersAfterHold;
	private int _helloHeld;
	private int _connections;
	private int _watchesToEndEarly;
	private int _reachedAnother;

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

	/// <summary>How many watches ended because they reached another broker than the relay named.</summary>
	public int WatchesThatReachedAnother => Volatile.Read(ref _reachedAnother);

	/// <summary>
	/// Whether a held hello has been asked: the relay has published the connection it is for and is
	/// reading that broker, since it asks only once the connection is the one calls use.
	/// </summary>
	public bool HelloHeld => Volatile.Read(ref _helloHeld) == 1;

	public override string Name => "The fake broker";

	public override bool Reconnects { get; }

	/// <summary>Makes the next watch end at once, with its broker still there.</summary>
	public void EndNextWatchEarly() => Interlocked.Exchange(ref _watchesToEndEarly, 1);

	/// <summary>
	/// Keeps a watch whose connection closed from saying so until <see cref="ReleaseClosedWatches"/>,
	/// which lets a call notice the broker gone, and reconnect, before the watch does.
	/// </summary>
	public void HoldClosedWatches() =>
		_closedWatchesHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

	public void ReleaseClosedWatches() => _closedWatchesHeld?.TrySetResult();

	/// <summary>
	/// Makes the hello on connection <paramref name="connection"/> wait until the returned source is
	/// completed and then say nothing, as a tray still starting answers it: late, or not at all.
	/// </summary>
	public TaskCompletionSource SilenceHelloOn(int connection) => HoldHelloOn(connection, answers: false);

	/// <summary>
	/// Makes the hello on connection <paramref name="connection"/> wait until the returned source is
	/// completed, which holds the relay's adopt of that connection open, and then answer or not.
	/// </summary>
	public TaskCompletionSource HoldHelloOn(int connection, bool answers)
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_silentHello = release;
		_helloAnswersAfterHold = answers;
		Volatile.Write(ref _silentHelloOn, connection);

		return release;
	}

	public override async Task<McpClient> ConnectAsync(TimeSpan within, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + within;

		while (true)
		{
			if (_current is { } broker)
			{
				var client = await broker.ConnectAsync(cancellationToken);
				_owners[client] = broker;
				_numbers[client] = Interlocked.Increment(ref _connections);

				return client;
			}

			if (DateTime.UtcNow >= deadline) throw new HttpRequestException("Nothing is listening.");

			await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
		}
	}

	public override async Task<RelayHello?> HelloAsync(McpClient client, CancellationToken cancellationToken)
	{
		if (!_owners.TryGetValue(client, out var broker)) return null;

		var silenced = _numbers.GetValueOrDefault(client) == Volatile.Read(ref _silentHelloOn);
		if (silenced && _silentHello is { } release)
		{
			Volatile.Write(ref _helloHeld, 1);
			await release.Task.WaitAsync(cancellationToken);
			if (!_helloAnswersAfterHold) return null;
		}

		return new RelayHello
		{
			Instance = broker.Instance,
			StartedUtc = DateTimeOffset.UtcNow,
			Build = broker.Build,
			RelayProtocol = broker.RelayProtocol,
			Relays = 1,
		};
	}

	public override async Task<WatchEnd> WatchAsync(McpClient client, string? instance, CancellationToken cancellationToken)
	{
		_watching = _owners.GetValueOrDefault(client);

		if (Interlocked.Exchange(ref _watchesToEndEarly, 0) == 1) return WatchEnd.Closed;

		// The watch is a request of its own to the endpoint, so it reaches whichever broker listens
		// there now, whatever the relay's connection is to; one other than the relay believes it holds
		// ends the watch at once, as the real one does.
		if (_current is { } listening && instance is not null && !string.Equals(listening.Instance, instance, StringComparison.Ordinal))
		{
			Interlocked.Increment(ref _reachedAnother);
			return WatchEnd.ReachedAnother(listening.Instance);
		}

		await client.Completion.WaitAsync(cancellationToken);
		if (_closedWatchesHeld is { } held) await held.Task.WaitAsync(cancellationToken);

		return WatchEnd.Closed;
	}
}
