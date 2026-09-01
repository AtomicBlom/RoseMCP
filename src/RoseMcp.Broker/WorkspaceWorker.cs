using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Client;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

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
	/// How the initial load is labelled in the activity log. Not a tool name, because what someone
	/// watching cares about is that the solution is loading, not which call happens to be waiting.
	/// </summary>
	public const string LoadOperation = "load solution";

	/// <summary>
	/// The SDK's own options, not a hand-rolled equivalent. The worker serialises its results with
	/// these, and anything that differs -- string enums being the one that bit -- turns a working
	/// call into a deserialisation failure at the boundary.
	/// </summary>
	private static readonly JsonSerializerOptions SerializerOptions = McpJsonUtilities.DefaultOptions;

	private static readonly Dictionary<string, object?> EmptyArguments = [];

	private readonly McpClient _client;
	private readonly ActivityLog _activities;
	private readonly ILogger _logger;

	private WorkspaceWorker(string solutionPath, McpClient client, ActivityLog activities, ILogger logger)
	{
		SolutionPath = solutionPath;
		_client = client;
		_activities = activities;
		_logger = logger;
		StartedUtc = DateTime.UtcNow;
	}

	public string SolutionPath { get; }

	public DateTime StartedUtc { get; }

	/// <summary>
	/// The worker's process id, learned on connect. Held so memory can be sampled from outside
	/// the process, which keeps working when the worker itself has stopped answering.
	/// </summary>
	public int? ProcessId { get; private set; }

	/// <summary>Last managed heap size the worker reported while it was still responding.</summary>
	public long? ManagedHeapBytes { get; private set; }

	public WorkerExitReason ExitReason { get; private set; } = WorkerExitReason.Running;

	public bool IsAlive => ExitReason == WorkerExitReason.Running;

	public static async Task<WorkspaceWorker> StartAsync(
		string solutionPath,
		string workerPath,
		BrokerOptions options,
		ActivityLog activities,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken,
		WorkspaceBuildOverrides? build = null)
	{
		var logger = loggerFactory.CreateLogger<WorkspaceWorker>();

		var arguments = new List<string> { "--solution", solutionPath };
		if (options.NoRestore) arguments.Add("--no-restore");

		// MSBuild properties are per process, so this is the only place they can be applied: a
		// worker cannot change the configuration it loaded under without being restarted.
		if (build?.Configuration is { Length: > 0 } configuration)
		{
			arguments.Add("--configuration");
			arguments.Add(configuration);
		}

		if (build?.Platform is { Length: > 0 } platform)
		{
			arguments.Add("--platform");
			arguments.Add(platform);
		}

		foreach (var property in build?.Properties ?? [])
		{
			arguments.Add("--property");
			arguments.Add(property);
		}

		var transport = new StdioClientTransport(
			new StdioClientTransportOptions
			{
				Command = workerPath,
				Arguments = arguments,

				// Task Manager's Details tab shows this, which is how a human works out which of
				// several identical worker processes belongs to which solution.
				Name = $"rose-worker {Path.GetFileNameWithoutExtension(solutionPath)}",
				WorkingDirectory = Path.GetDirectoryName(solutionPath),
			},
			loggerFactory);

		logger.LogInformation("Starting a worker for {SolutionPath}.", solutionPath);

		var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);

		var worker = new WorkspaceWorker(solutionPath, client, activities, logger);
		await worker.RefreshProcessInfoAsync(cancellationToken);
		worker.BeginLoading();

		return worker;
	}

	/// <summary>
	/// Forwards a tool call, records it as an activity, and deserialises the worker's structured
	/// result.
	/// <para>
	/// Worker tools mirror the broker's one-for-one minus the workspace argument, so routing is a
	/// straight pass-through and the two schemas cannot drift apart.
	/// </para>
	/// <para>
	/// A progress sink is the calling client's, when it asked for one; progress reaches the
	/// activity log either way, so a long call shows up in the tray even when nobody else is
	/// watching. An operation overrides the activity's label, which is otherwise the tool name.
	/// </para>
	/// </summary>
	public async Task<T> CallAsync<T>(
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null,
		string? operation = null)
	{
		using var activity = _activities.Begin(SolutionPath, operation ?? tool, DescribeTarget(arguments), progress);

		try
		{
			return await SendAsync<T>(tool, arguments, activity, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			activity.Complete(ActivityOutcome.Cancelled);
			throw;
		}
		catch (Exception exception)
		{
			activity.Complete(ActivityOutcome.Failed, exception.Message);
			throw;
		}
	}

	public void MarkStopped(WorkerExitReason reason) => ExitReason = reason;

	/// <summary>
	/// Asks the worker who it is. Cheap by design -- it loads nothing -- so it is safe to call on
	/// connect before the solution has been opened. Deliberately untracked: bookkeeping calls in
	/// the activity list would bury the ones a human is actually looking for.
	/// </summary>
	public async Task RefreshProcessInfoAsync(CancellationToken cancellationToken)
	{
		try
		{
			var info = await SendAsync<WorkerInfo>(ToolNames.WorkerInfo, EmptyArguments, progress: null, cancellationToken);
			ProcessId = info.ProcessId;
			ManagedHeapBytes = info.ManagedHeapBytes;
		}
		catch (Exception exception)
		{
			// Memory reporting is a nicety. Losing it must not stop the workspace from opening.
			_logger.LogDebug(exception, "Could not read worker info for {SolutionPath}.", SolutionPath);
		}
	}

	/// <summary>
	/// Samples memory from the process table rather than asking the worker, so the numbers stay
	/// truthful for a worker that is wedged -- which is exactly when someone is looking at them.
	/// </summary>
	public WorkspaceSummary Describe()
	{
		long? workingSet = null;
		long? privateMemory = null;

		if (ProcessId is { } id)
		{
			try
			{
				using var process = System.Diagnostics.Process.GetProcessById(id);
				workingSet = process.WorkingSet64;
				privateMemory = process.PrivateMemorySize64;
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
			{
				// The process is gone. Reporting no numbers is more honest than reporting stale ones.
			}
		}

		return new WorkspaceSummary
		{
			SolutionPath = SolutionPath,
			DisplayName = Path.GetFileNameWithoutExtension(SolutionPath),
			Alive = IsAlive,
			ExitReason = ExitReason.ToString(),
			StartedUtc = StartedUtc,
			Uptime = DateTime.UtcNow - StartedUtc,
			ProcessId = ProcessId,
			WorkingSetBytes = workingSet,
			PrivateMemoryBytes = privateMemory,
			ManagedHeapBytes = ManagedHeapBytes,
			Running = _activities.Running(SolutionPath),
			Recent = _activities.Recent(SolutionPath),
		};
	}

	/// <summary>
	/// Asks for status straight away, purely so the load has something to report progress against.
	/// <para>
	/// A worker starts loading the moment it launches, whether or not anyone has called it, and
	/// progress notifications only exist in the context of a request. With no call in flight the
	/// first half-minute of a large solution is invisible -- which is exactly what a tray reload
	/// produces, since no client is waiting on it. The work is not wasted: this is the same
	/// design-time build and generator pass the first real call would have paid for.
	/// </para>
	/// </summary>
	private void BeginLoading() => _ = FollowLoadAsync();

	private async Task FollowLoadAsync()
	{
		try
		{
			await CallAsync<WorkspaceStatusReport>(
				ToolNames.WorkspaceStatus,
				EmptyArguments,
				CancellationToken.None,
				operation: LoadOperation);
		}
		catch (Exception exception)
		{
			// Nothing is waiting on this result. A load failure is reported to whoever calls next,
			// and the activity itself already records that it failed.
			_logger.LogDebug(exception, "Following the load of {SolutionPath} ended early.", SolutionPath);
		}
	}

	private async Task<T> SendAsync<T>(
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		IProgress<ProgressNotificationValue>? progress,
		CancellationToken cancellationToken)
	{
		ModelContextProtocol.Protocol.CallToolResult result;
		try
		{
			// Not CallToolAsync: it abandons the wait without telling the worker, which then finishes
			// the whole operation. Reads on a workspace are ordered, so that abandoned work is the
			// delay before the next call on it can start.
			result = await CancellableToolCall.InvokeAsync(_client, tool, arguments, progress, cancellationToken);
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

	/// <summary>
	/// What the call is aimed at, for the activity row. Arguments are all the broker knows about a
	/// call, and "rose_rename_symbol" on its own answers none of the questions someone watching a
	/// queue of them would ask.
	/// </summary>
	private static string? DescribeTarget(IReadOnlyDictionary<string, object?> arguments)
	{
		if (Text(arguments, "filePath") is { } filePath)
		{
			var name = Path.GetFileName(filePath);
			var position = Text(arguments, "line") is { } line ? $"{name}:{line}" : name;

			return Text(arguments, "newName") is { } newName ? $"{position} to {newName}" : position;
		}

		return Text(arguments, "target")
			?? Text(arguments, "hintName")
			?? Text(arguments, "query")
			?? Text(arguments, "project")
			?? Text(arguments, "scope");
	}

	private static string? Text(IReadOnlyDictionary<string, object?> arguments, string key) =>
		arguments.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

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
