using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

/// <summary>
/// Owns this worker's single solution for the life of the process.
/// <para>
/// Loading starts as soon as the host does rather than on first use, so the expensive design-time
/// build overlaps with the client finishing its handshake. Callers await the same load task, which
/// means concurrent first calls cost one load rather than several.
/// </para>
/// </summary>
public sealed class WorkspaceHost(
	WorkerOptions options,
	SolutionLoader loader,
	ILoggerFactory loggerFactory,
	ILogger<WorkspaceHost> logger) : IHostedService, IAsyncDisposable
{
	private readonly CancellationTokenSource _shutdown = new();
	private Task<WorkspaceSession>? _start;
	private volatile WorkspaceStatusReport? _faulted;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_start = Task.Run(StartSessionAsync, CancellationToken.None);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

	/// <summary>
	/// Current status, reconciled with disk first so the counts describe the solution as it is now
	/// rather than as it was at load. Never throws: a load failure becomes a
	/// <see cref="WorkspaceState.Faulted"/> report, because a caller asking what state the
	/// workspace is in deserves an answer rather than an exception.
	/// </summary>
	public async Task<WorkspaceStatusReport> GetStatusAsync(CancellationToken cancellationToken)
	{
		if (_faulted is not null)
			return _faulted;

		try
		{
			var session = await StartedAsync();
			var snapshot = await session.ReadAsync(cancellationToken);

			var report = await WorkspaceStatusReporter.DescribeAsync(
				snapshot.Solution,
				options.SolutionPath,
				[],
				restore: null,
				snapshot.Revision,
				loadSeconds: 0,
				cancellationToken);

			return report with { DegradedReasons = [.. report.DegradedReasons, .. snapshot.Notices] };
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			logger.LogError(exception, "Loading {SolutionPath} failed.", options.SolutionPath);
			return _faulted = Fault(exception);
		}
	}

	/// <summary>
	/// A snapshot to analyse, already ordered behind every pending mutation and reconciled with
	/// disk. Unlike <see cref="GetStatusAsync"/> this throws, because a caller wanting to read code
	/// can do nothing useful with a failed load.
	/// </summary>
	public async Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) =>
		await (await StartedAsync()).ReadAsync(cancellationToken);

	public async Task<WorkspaceSession> SessionAsync() => await StartedAsync();

	private async Task<WorkspaceSession> StartSessionAsync()
	{
		var load = await loader.LoadAsync(options, _shutdown.Token);
		return WorkspaceSession.Create(load, loader, options, loggerFactory.CreateLogger<WorkspaceSession>());
	}

	private Task<WorkspaceSession> StartedAsync() =>
		_start ?? throw new InvalidOperationException("The workspace host has not been started.");

	private WorkspaceStatusReport Fault(Exception exception) => new()
	{
		SolutionPath = options.SolutionPath,
		State = WorkspaceState.Faulted,
		Revision = 0,
		Projects = [],
		LoadDiagnostics = [],
		DegradedReasons = [$"Loading the solution failed: {exception.Message}"],
	};

	public async ValueTask DisposeAsync()
	{
		await _shutdown.CancelAsync();

		if (_start is not null)
		{
			try
			{
				await (await _start).DisposeAsync();
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "The workspace session did not shut down cleanly.");
			}
		}

		_shutdown.Dispose();
	}
}
