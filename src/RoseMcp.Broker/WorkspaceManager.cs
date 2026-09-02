using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol;

namespace RoseMcp.Broker;

/// <summary>
/// The registry of open workspaces, and the only place workers are started or stopped.
/// <para>
/// Registered as a singleton so it is shared across every session. In http mode that is what lets a
/// reconnecting client reattach to an already-loaded solution instead of paying the load cost again.
/// </para>
/// </summary>
public sealed class WorkspaceManager(
	IOptions<BrokerOptions> options,
	ILoggerFactory loggerFactory,
	ILogger<WorkspaceManager> logger) : IAsyncDisposable
{
	private readonly Dictionary<string, WorkspaceWorker> _workers = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// MSBuild properties asked for at reload, per solution. Kept because they belong to the worker's
	/// command line, and a worker replaced after a crash would otherwise lose them.
	/// </summary>
	private readonly Dictionary<string, WorkspaceBuildOverrides> _buildOverrides =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly BrokerOptions _options = options.Value;

	/// <summary>
	/// What every worker is doing. Owned here rather than injected because the manager is the only
	/// thing that starts, stops, and calls workers, so it is the only thing that could fill it in.
	/// </summary>
	public ActivityLog Activities { get; } = new();

	/// <summary>Open workspaces, for status reporting and the tray UI.</summary>
	public IReadOnlyList<WorkspaceWorker> Workers
	{
		get
		{
			lock (_workers)
			{
				return [.. _workers.Values];
			}
		}
	}

	/// <summary>
	/// One row per open workspace, memory and in-flight work included. The same model backs the
	/// tray window and GET /admin/workspaces, so the UI can never show something the API disagrees
	/// with.
	/// </summary>
	public IReadOnlyList<Contracts.WorkspaceSummary> Describe() => [.. Workers.Select(worker => worker.Describe())];

	/// <summary>
	/// The worker for <paramref name="path"/>, starting one if needed.
	/// <para>
	/// A dead worker is replaced rather than reported. Workers die for ordinary reasons -- the
	/// solution was deleted and has come back, a hard reload killed one, memory ran out -- and
	/// making the caller retry after each of those would be needless ceremony.
	/// </para>
	/// </summary>
	public async Task<WorkspaceWorker> GetOrStartAsync(string? path, CancellationToken cancellationToken)
	{
		var solutionPath = ResolveOrInfer(path);

		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (_workers.TryGetValue(solutionPath, out var existing))
			{
				if (existing.IsAlive) return existing;

				logger.LogInformation(
					"Replacing the worker for {SolutionPath}; it stopped with {Reason}.",
					solutionPath,
					existing.ExitReason);

				await existing.DisposeAsync();
				_workers.Remove(solutionPath);
				Activities.Forget(solutionPath);
			}

			if (!File.Exists(solutionPath))
			{
				throw new InvalidOperationException($"The solution no longer exists at {solutionPath}.");
			}

			var worker = await StartAsync(solutionPath, cancellationToken);

			_workers[solutionPath] = worker;
			return worker;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Spawns a worker, tracked so the wait is visible. Process launch and the MCP handshake are
	/// only a second or so, but reporting them separately is what distinguishes a worker that is
	/// slow to start from a solution that is slow to load.
	/// </summary>
	private async Task<WorkspaceWorker> StartAsync(string solutionPath, CancellationToken cancellationToken)
	{
		using var activity = Activities.Begin(solutionPath, "start worker");

		try
		{
			return await WorkspaceWorker.StartAsync(
				solutionPath,
				WorkerLauncher.ResolveWorkerPath(_options),
				_options,
				Activities,
				loggerFactory,
				cancellationToken,
				_buildOverrides.GetValueOrDefault(solutionPath));
		}
		catch (Exception exception)
		{
			activity.Complete(Contracts.ActivityOutcome.Failed, exception.Message);
			throw;
		}
	}

	/// <summary>
	/// Forwards a tool call, replacing the worker and retrying once if it turns out to be dead.
	/// <para>
	/// Retrying is only safe when the tool is read-only. A rename that died part-way through may
	/// already have written some of its files, so replaying it could apply the change twice; those
	/// callers get a clear failure and decide for themselves.
	/// </para>
	/// </summary>
	public async Task<T> CallAsync<T>(
		string? workspace,
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		bool retryIfWorkerDied,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null)
	{
		var worker = await GetOrStartAsync(workspace, cancellationToken);

		try
		{
			return await worker.CallAsync<T>(tool, arguments, cancellationToken, progress);
		}
		catch (WorkerUnavailableException) when (retryIfWorkerDied)
		{
			logger.LogInformation("Restarting the worker for {SolutionPath} and retrying {Tool}.", worker.SolutionPath, tool);

			var replacement = await RestartAsync(worker.SolutionPath, cancellationToken);
			return await replacement.CallAsync<T>(tool, arguments, cancellationToken, progress);
		}
	}

	/// <summary>Stops a worker and forgets it. Reopening starts a fresh process.</summary>
	public async Task<bool> CloseAsync(string? path, CancellationToken cancellationToken)
	{
		var solutionPath = ResolveOrInfer(path);

		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!_workers.Remove(solutionPath, out var worker)) return false;

			await worker.DisposeAsync();

			// The history belonged to that process. Keeping it would attribute the old worker's
			// work to whatever starts next.
			Activities.Forget(solutionPath);
			return true;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Kills and restarts a worker. This is the only reliable way to pick up a rebuilt analyzer or
	/// generator: assembly loading is one-way, so a process that has loaded the old one can never
	/// see the new one.
	/// </summary>
	public async Task<WorkspaceWorker> RestartAsync(
		string? path,
		CancellationToken cancellationToken,
		WorkspaceBuildOverrides? build = null)
	{
		var solutionPath = ResolveOrInfer(path);

		// Remembered rather than applied once, so a worker that dies and is replaced later comes back
		// under the properties that were asked for rather than silently reverting.
		if (build is not null) _buildOverrides[solutionPath] = build;

		await CloseAsync(solutionPath, cancellationToken);

		return await GetOrStartAsync(solutionPath, cancellationToken);
	}

	/// <summary>
	/// Works out which workspace a call means.
	/// <para>
	/// Falls back to discovering a solution from the working directory rather than demanding an
	/// explicit open first. A tool that needs a setup call before it answers anything is a tool that
	/// gets skipped in favour of grep, so the zero-argument path has to work.
	/// </para>
	/// <para>
	/// The two failures here throw McpException rather than ArgumentException, and the difference is
	/// the whole point: the SDK turns an unrecognised exception into "An error occurred invoking
	/// 'rose_diagnostics'." and drops the message, so a caller that could have fixed the call itself
	/// is told nothing. Both of these know exactly what the caller should do next, and both say so.
	/// </para>
	/// </summary>
	private string ResolveOrInfer(string? path)
	{
		if (!string.IsNullOrWhiteSpace(path)) return Resolved(path);

		lock (_workers)
		{
			if (_workers.Count == 1) return _workers.Keys.First();

			if (_workers.Count > 1)
			{
				throw new McpException(
					$"{_workers.Count} workspaces are open, so the workspace argument is required. Open: "
						+ string.Join(", ", _workers.Keys));
			}
		}

		try
		{
			return Resolved(_options.DefaultWorkspaceRoot);
		}
		catch (ArgumentException exception)
		{
			throw new McpException(
				"No workspace is open and no solution was found near "
					+ $"{_options.DefaultWorkspaceRoot}. Pass a path to a solution, project, or any file "
					+ "inside one.",
				exception);
		}
	}

	/// <summary>
	/// Resolves a path, recording what it chose between when there was a choice.
	/// <para>
	/// Only the contested case is logged, and it is logged whether or not it went on to succeed. A
	/// wrong choice here is close to undiagnosable from the answer -- searching the wrong solution
	/// returns nothing, which reads exactly like searching the right one and finding nothing -- so
	/// the candidates have to be in the log before anyone knows to look for them.
	/// </para>
	/// </summary>
	private string Resolved(string path)
	{
		var choice = SolutionResolver.Choose(path);

		if (choice.WasContested)
		{
			logger.LogDebug(
				"Resolved {Path} to {SolutionPath}, {Reason}, from: {Candidates}.",
				path,
				choice.SolutionPath,
				choice.Reason,
				string.Join(", ", choice.Candidates));
		}

		return choice.SolutionPath;
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var worker in Workers)
		{
			await worker.DisposeAsync();
		}

		lock (_workers)
		{
			_workers.Clear();
		}

		_gate.Dispose();
	}
}
