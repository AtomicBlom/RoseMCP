using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Collects presses of the in-app panel's Ask button off the provider, and holds the latest one worth showing.
/// <para>
/// What it has seen is the provider's, so it is tied to the connection it was seen on: a provider that reconnects is
/// asked again from nothing, and a press it answers with is taken whatever its number. A press older than
/// <see cref="FreshFor"/> is acknowledged, so the panel's chip goes back to rest, and not kept: the panel outlives
/// every session, so a press made while no host was connected arrives with the next one, and opening a window about
/// it then would answer a question nobody is waiting on.
/// </para>
/// <para>
/// Not thread-safe for <see cref="Collect"/>: the caller holds the request lock over the pipe, which is what
/// serialises it. <see cref="Latest"/> may be read from anywhere.
/// </para>
/// </summary>
public sealed class XamlAskCollector
{
	/// <summary>How old a press can be and still be kept to open a window about.</summary>
	public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

	private int _connection = -1;
	private long _seen;
	private LiveXamlAsk? _latest;

	/// <summary>The latest fresh press collected, or null before there has been one.</summary>
	public LiveXamlAsk? Latest => Volatile.Read(ref _latest);

	/// <summary>
	/// Asks the provider for its latest press and keeps it if it is new and fresh.
	/// </summary>
	/// <returns>The press kept by this call, or null when nothing new was kept.</returns>
	public LiveXamlAsk? Collect(XamlProviderPipe pipe, TimeSpan bound, DateTime nowUtc)
	{
		ArgumentNullException.ThrowIfNull(pipe);

		var connection = pipe.Connections;
		if (connection != _connection)
		{
			_connection = connection;
			_seen = 0;
		}

		var (answered, press) = XamlPanelRequests.Ask(pipe, _seen, bound);
		if (!answered || press is null || press.Sequence == _seen) return null;

		_seen = press.Sequence;

		var ask = LiveXamlAsk.From(press, nowUtc);
		if (nowUtc - ask.PressedUtc > FreshFor) return null;

		Volatile.Write(ref _latest, ask);
		return ask;
	}
}
