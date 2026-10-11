using ModelContextProtocol.Client;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// A broker an installed relay started itself, over stdio, because no tray was running.
/// <para>
/// It is the install root's server, so it runs the build the last deploy put down rather than the
/// relay's own, older assemblies. It is not replaced when it goes: it runs out of the files a deploy
/// overwrites, so the deploy stops it, and a relay that started another straight away would be
/// holding those files again while the copy was still under way. So a relay whose child has gone
/// ends too, and its session reconnects -- the one case where a deploy still costs a session its
/// connection, and the reason the tray is the way to run RoseMCP.
/// </para>
/// </summary>
/// <param name="executable">The install root's server.</param>
/// <param name="arguments">What it is started with, which keeps it from relaying in turn.</param>
/// <param name="workingDirectory">The session's own directory, which the child takes as the caller's.</param>
/// <param name="loggerFactory">Where the connection logs.</param>
public sealed class ChildBrokerUpstream(
	string executable,
	IReadOnlyList<string> arguments,
	string workingDirectory,
	ILoggerFactory loggerFactory) : RelayUpstream
{
	/// <inheritdoc/>
	public override string Name => $"The broker this session started from {executable}";

	/// <inheritdoc/>
	public override bool Reconnects => false;

	/// <inheritdoc/>
	public override Task<McpClient> ConnectAsync(TimeSpan within, CancellationToken cancellationToken)
	{
		var transport = new StdioClientTransport(
			new StdioClientTransportOptions
			{
				Command = executable,
				Arguments = [.. arguments],
				WorkingDirectory = workingDirectory,
				Name = "RoseMcp.Server",

				// The child writes its own log file. Its stderr is the same lines again, at every level,
				// and relaying them would double this session's log for nothing.
				StandardErrorLines = _ => { },
			},
			loggerFactory);

		// Its read loop serves every call this session makes from now on, so it carries no call's context.
		return Detached.Run(() => McpClient.CreateAsync(
			transport, ChildHostHandshake.Options(within), loggerFactory, cancellationToken));
	}

	/// <summary>Nothing to ask: a child this relay started is new by construction.</summary>
	public override Task<RelayHello?> HelloAsync(McpClient client, CancellationToken cancellationToken) =>
		Task.FromResult<RelayHello?>(null);

	/// <summary>Completes when the child's connection does, which is when the child exits.</summary>
	public override async Task<WatchEnd> WatchAsync(McpClient client, string? instance, CancellationToken cancellationToken)
	{
		await client.Completion.WaitAsync(cancellationToken);
		return WatchEnd.Closed;
	}
}
