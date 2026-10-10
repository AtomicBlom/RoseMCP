using ModelContextProtocol.Client;

using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// The broker a relay forwards to, and how it reaches one, learns which one it reached, and notices
/// losing it.
/// <para>
/// Two kinds: the tray, over http, which a deploy restarts and the relay reconnects to; and a broker
/// the relay started itself, over stdio, when no tray was running, which cannot outlive the deploy
/// that replaces its files and is not replaced. Everything the relay decides -- whether the tools
/// changed, what the first result says -- is the same for both, so it lives in
/// <see cref="TrayRelay"/> and this says only how to connect.
/// </para>
/// </summary>
public abstract class RelayUpstream : IAsyncDisposable
{
	/// <summary>The broker named for the start of a sentence: "The RoseMCP tray at http://127.0.0.1:5077/".</summary>
	public abstract string Name { get; }

	/// <summary>Whether a lost broker can be replaced by connecting again.</summary>
	public abstract bool Reconnects { get; }

	/// <summary>Opens a connection, trying until <paramref name="within"/> elapses.</summary>
	public abstract Task<McpClient> ConnectAsync(TimeSpan within, CancellationToken cancellationToken);

	/// <summary>
	/// Which broker <paramref name="client"/> reached, or null where it cannot say: a broker from
	/// before the relay protocol, or one this relay started and so knows to be new.
	/// </summary>
	public abstract Task<RelayHello?> HelloAsync(McpClient client, CancellationToken cancellationToken);

	/// <summary>
	/// Completes when the broker behind <paramref name="client"/> may have gone; the caller asks it
	/// before believing so. A watch that cannot be held waits until cancelled, leaving a lost broker
	/// to be found by the next call.
	/// </summary>
	/// <param name="client">The connection the relay is using.</param>
	/// <param name="instance">The broker instance the relay believes it is connected to, where it knows one.</param>
	/// <param name="cancellationToken">Ends the watch, when the relay does.</param>
	public abstract Task WatchAsync(McpClient client, string? instance, CancellationToken cancellationToken);

	/// <inheritdoc/>
	public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
