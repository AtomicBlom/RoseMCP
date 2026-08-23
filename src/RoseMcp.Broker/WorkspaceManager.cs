using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly BrokerOptions _options = options.Value;

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
	/// One row per open workspace, memory included. The same model backs the tray window and
	/// GET /admin/workspaces, so the UI can never show something the API disagrees with.
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
			}

			if (!File.Exists(solutionPath))
			{
				throw new InvalidOperationException($"The solution no longer exists at {solutionPath}.");
			}

			var worker = await WorkspaceWorker.StartAsync(
				solutionPath,
				WorkerLauncher.ResolveWorkerPath(_options),
				_options,
				loggerFactory,
				cancellationToken);

			_workers[solutionPath] = worker;
			return worker;
		}
		finally
		{
			_gate.Release();
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
		CancellationToken cancellationToken)
	{
		var worker = await GetOrStartAsync(workspace, cancellationToken);

		try
		{
			return await worker.CallAsync<T>(tool, arguments, cancellationToken);
		}
		catch (WorkerUnavailableException) when (retryIfWorkerDied)
		{
			logger.LogInformation("Restarting the worker for {SolutionPath} and retrying {Tool}.", worker.SolutionPath, tool);

			var replacement = await RestartAsync(worker.SolutionPath, cancellationToken);
			return await replacement.CallAsync<T>(tool, arguments, cancellationToken);
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
	public async Task<WorkspaceWorker> RestartAsync(string? path, CancellationToken cancellationToken)
	{
		var solutionPath = ResolveOrInfer(path);
		await CloseAsync(solutionPath, cancellationToken);

		return await GetOrStartAsync(solutionPath, cancellationToken);
	}

	/// <summary>
	/// Works out which workspace a call means. With exactly one open and no path given, the answer
	/// is unambiguous -- demanding the path anyway would be pedantry.
	/// </summary>
	/// <summary>
	/// Works out which workspace a call means.
	/// <para>
	/// Falls back to discovering a solution from the working directory rather than demanding an
	/// explicit open first. A tool that needs a setup call before it answers anything is a tool that
	/// gets skipped in favour of grep, so the zero-argument path has to work.
	/// </para>
	/// </summary>
	private string ResolveOrInfer(string? path)
	{
		if (!string.IsNullOrWhiteSpace(path)) return SolutionResolver.Resolve(path);

		lock (_workers)
		{
			if (_workers.Count == 1) return _workers.Keys.First();

			if (_workers.Count > 1)
			{
				throw new ArgumentException(
					$"{_workers.Count} workspaces are open, so the workspace argument is required. Open: "
						+ string.Join(", ", _workers.Keys));
			}
		}

		try
		{
			return SolutionResolver.Resolve(_options.DefaultWorkspaceRoot);
		}
		catch (ArgumentException exception)
		{
			throw new ArgumentException(
				"No workspace is open and no solution was found near "
					+ $"{_options.DefaultWorkspaceRoot}. Pass a path to a solution, project, or any file "
					+ "inside one.",
				exception);
		}
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
