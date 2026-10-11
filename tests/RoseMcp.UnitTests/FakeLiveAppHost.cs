using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// A live-app host in this process, over a pair of pipes, standing in for the real one where no debugger can run:
/// it says it is attached to the process its target names and serving, and detaches when asked. Enough for the
/// session manager's rules about who a session belongs to and which process it debugs, which is all a test of those
/// needs of a host.
/// </summary>
internal sealed class FakeLiveAppHost : IAsyncDisposable
{
	private readonly ConcurrentBag<(McpServer Server, CancellationTokenSource Stop, Task Running)> _hosts = [];
	private int _started;

	/// <summary>How many hosts have been started, which is how a test sees that a start reached no host at all.</summary>
	public int Started => Volatile.Read(ref _started);

	/// <summary>A transport to a fresh host for <paramref name="target"/>, as <c>BrokerOptions.LiveAppHost</c> asks for one.</summary>
	public IClientTransport For(LiveAppTarget target)
	{
		Interlocked.Increment(ref _started);

		var toServer = new Pipe();
		var toClient = new Pipe();
		var processId = target.ProcessId ?? 7000 + Started;

		var options = new McpServerOptions
		{
			ServerInfo = new Implementation { Name = "fake-live-app-host", Version = "0.0.0" },
			Handlers = new McpServerHandlers
			{
				CallToolHandler = (context, _) => ValueTask.FromResult(Answer(context.Params!.Name, processId)),
			},
		};

		var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
		var stop = new CancellationTokenSource();
		_hosts.Add((server, stop, server.RunAsync(stop.Token)));

		return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
	}

	private static CallToolResult Answer(string tool, int processId)
	{
		var info = new LiveAppInfo
		{
			HostProcessId = 1,
			Architecture = TargetArchitecture.X64,
			State = tool == ToolNames.LiveAppDetach ? LiveAppSessionState.Ended : LiveAppSessionState.Ready,
			TargetProcessId = processId,
			Execution = LiveExecutionState.Running,
			XamlStack = XamlStack.Unknown,
			XamlStackReason = "a fake host reads no modules",
			XamlProvider = LiveXamlProvider.None,
		};

		return new CallToolResult
		{
			Content = [],
			StructuredContent = JsonSerializer.SerializeToElement(info, McpJsonUtilities.DefaultOptions),
		};
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var (server, stop, running) in _hosts)
		{
			await stop.CancelAsync();

			try
			{
				await running;
			}
			catch (OperationCanceledException)
			{
				// Stopped, as asked.
			}

			await server.DisposeAsync();
			stop.Dispose();
		}
	}
}
