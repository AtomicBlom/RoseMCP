using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace RoslynMcp.Broker;

/// <summary>Why a worker is no longer serving requests.</summary>
public enum WorkerExitReason
{
	Running,

	/// <summary>The solution went away and the worker shut itself down. Expected, not a failure.</summary>
	SolutionUnloaded,

	/// <summary>The process died on its own.</summary>
	Crashed,

	/// <summary>The broker stopped it, usually for a hard reload.</summary>
	StoppedByBroker,
}

/// <summary>
/// One worker process and the MCP client talking to it.
/// <para>
/// Worker exit is treated as ordinary rather than exceptional. Workers are expected to die: the
/// solution can be deleted, a hard reload kills one deliberately, and a Roslyn host holding a large
/// solution can run out of memory. The broker's job is to notice, say why, and keep serving.
/// </para>
/// </summary>
public sealed class WorkspaceWorker : IAsyncDisposable
{
	/// <summary>
	/// The SDK's own options, not a hand-rolled equivalent. The worker serialises its results with
	/// these, and anything that differs -- string enums being the one that bit -- turns a working
	/// call into a deserialisation failure at the boundary.
	/// </summary>
	private static readonly JsonSerializerOptions SerializerOptions = McpJsonUtilities.DefaultOptions;

	private readonly McpClient _client;
	private readonly ILogger _logger;

	private WorkspaceWorker(string solutionPath, McpClient client, ILogger logger)
	{
		SolutionPath = solutionPath;
		_client = client;
		_logger = logger;
		StartedUtc = DateTime.UtcNow;
	}

	public string SolutionPath { get; }

	public DateTime StartedUtc { get; }

	public WorkerExitReason ExitReason { get; private set; } = WorkerExitReason.Running;

	public bool IsAlive => ExitReason == WorkerExitReason.Running;

	public static async Task<WorkspaceWorker> StartAsync(
		string solutionPath,
		string workerPath,
		BrokerOptions options,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		var logger = loggerFactory.CreateLogger<WorkspaceWorker>();

		var arguments = new List<string> { "--solution", solutionPath };
		if (options.NoRestore) arguments.Add("--no-restore");

		var transport = new StdioClientTransport(
			new StdioClientTransportOptions
			{
				Command = workerPath,
				Arguments = arguments,

				// Task Manager's Details tab shows this, which is how a human works out which of
				// several identical worker processes belongs to which solution.
				Name = $"roslyn-worker {Path.GetFileNameWithoutExtension(solutionPath)}",
				WorkingDirectory = Path.GetDirectoryName(solutionPath),
			},
			loggerFactory);

		logger.LogInformation("Starting a worker for {SolutionPath}.", solutionPath);

		var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);

		return new WorkspaceWorker(solutionPath, client, logger);
	}

	/// <summary>
	/// Forwards a tool call and deserialises the worker's structured result.
	/// <para>
	/// Worker tools mirror the broker's one-for-one minus the workspace argument, so routing is a
	/// straight pass-through and the two schemas cannot drift apart.
	/// </para>
	/// </summary>
	public async Task<T> CallAsync<T>(
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		CancellationToken cancellationToken)
	{
		ModelContextProtocol.Protocol.CallToolResult result;
		try
		{
			result = await _client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
		}
		catch (Exception exception) when (IsTransportFailure(exception))
		{
			// The real call is the honest liveness test. Pinging first would add a round trip to
			// every request and still answer for a moment that has already passed.
			ExitReason = WorkerExitReason.Crashed;
			_logger.LogWarning(exception, "The worker for {SolutionPath} died during {Tool}.", SolutionPath, tool);

			throw new WorkerUnavailableException(SolutionPath, exception);
		}

		if (result.IsError == true)
		{
			var message = string.Join(
				Environment.NewLine,
				result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text));

			throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
				? $"The worker for {SolutionPath} reported an error running {tool}."
				: message);
		}

		if (result.StructuredContent is null)
		{
			throw new InvalidOperationException($"The worker returned no structured content for {tool}.");
		}

		return result.StructuredContent.Value.Deserialize<T>(SerializerOptions)
			?? throw new InvalidOperationException($"Could not read the worker's {tool} result.");
	}

	public void MarkStopped(WorkerExitReason reason) => ExitReason = reason;

	/// <summary>
	/// Whether a failure means the worker is gone rather than the request being bad. A tool that
	/// throws is a normal error result; a transport that closes is a dead process.
	/// </summary>
	private static bool IsTransportFailure(Exception exception) => exception
		is ClientTransportClosedException
		or IOException
		or ObjectDisposedException
		or InvalidOperationException { Source: "ModelContextProtocol.Core" };

	public async ValueTask DisposeAsync()
	{
		if (IsAlive) ExitReason = WorkerExitReason.StoppedByBroker;

		try
		{
			// Disposing the client closes the worker's stdin, which is what tells it to exit. That
			// is the same mechanism that stops workers outliving a broker that dies.
			await _client.DisposeAsync();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "The worker for {SolutionPath} did not shut down cleanly.", SolutionPath);
		}
	}
}

/// <summary>
/// The worker for a workspace died. Distinct from a tool failing, because the workspace can be
/// brought back by starting a fresh worker whereas a bad request cannot.
/// </summary>
public sealed class WorkerUnavailableException(string solutionPath, Exception inner)
	: InvalidOperationException($"The worker for {solutionPath} is no longer running.", inner)
{
	public string SolutionPath { get; } = solutionPath;
}
