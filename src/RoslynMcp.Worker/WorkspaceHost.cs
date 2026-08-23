using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

/// <summary>
/// Owns this worker's single solution for the life of the process.
/// <para>
/// Loading starts as soon as the host does rather than on first use, so the expensive design-time
/// build overlaps with the client finishing its handshake. Callers await the same load task, which
/// means concurrent first calls cost one load, not several.
/// </para>
/// </summary>
public sealed class WorkspaceHost(
	WorkerOptions options,
	SolutionLoader loader,
	ILogger<WorkspaceHost> logger) : IHostedService, IDisposable
{
	private readonly CancellationTokenSource _shutdown = new();
	private Task<LoadResult>? _load;
	private volatile WorkspaceStatusReport? _faulted;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_load = Task.Run(() => loader.LoadAsync(options, _shutdown.Token), CancellationToken.None);
		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _shutdown.CancelAsync();

		if (_load is null)
			return;

		try
		{
			(await _load).Workspace.Dispose();
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Workspace was not disposed cleanly during shutdown.");
		}
	}

	/// <summary>
	/// The current status. Never throws: a load failure becomes a
	/// <see cref="WorkspaceState.Faulted"/> report carrying the reason, because a caller asking
	/// what state the workspace is in deserves an answer rather than an exception.
	/// </summary>
	public async Task<WorkspaceStatusReport> GetStatusAsync()
	{
		if (_faulted is not null)
			return _faulted;

		try
		{
			return (await Loaded()).Report;
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Loading {SolutionPath} failed.", options.SolutionPath);
			return _faulted = Fault(exception);
		}
	}

	/// <summary>
	/// The loaded solution snapshot. Unlike <see cref="GetStatusAsync"/> this does throw, because a
	/// caller wanting to analyse code cannot do anything useful with a failed load.
	/// </summary>
	public async Task<Solution> GetSolutionAsync() => (await Loaded()).Workspace.CurrentSolution;

	private Task<LoadResult> Loaded() =>
		_load ?? throw new InvalidOperationException("The workspace host has not been started.");

	private WorkspaceStatusReport Fault(Exception exception) => new()
	{
		SolutionPath = options.SolutionPath,
		State = WorkspaceState.Faulted,
		Revision = 0,
		Projects = [],
		LoadDiagnostics = [],
		DegradedReasons = [$"Loading the solution failed: {exception.Message}"],
	};

	public void Dispose() => _shutdown.Dispose();
}
