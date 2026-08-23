using System.Threading.Channels;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace RoslynMcp.Worker;

/// <summary>
/// The consistency core: one solution, one writer, and a guarantee that no read is ever served
/// from a snapshot older than disk.
/// <para>
/// Every operation is queued onto a single-consumer channel, so mutations are strictly ordered.
/// Reads queue a barrier that drains everything ahead of it, reconciles the snapshot with disk,
/// and returns an immutable <see cref="WorkspaceSnapshot"/>. The expensive part of a read --
/// compiling, running analyzers, finding references -- then happens off the writer against that
/// snapshot, so concurrent reads still parallelise. Roslyn's immutability is what makes both halves
/// of that safe.
/// </para>
/// <para>
/// The MSBuildWorkspace is used only as a loader. Its snapshot is forked on the first read and the
/// session owns the authoritative <see cref="Solution"/> from then on, because the only public way
/// to push changes back into a Workspace is TryApplyChanges, which writes to disk -- the opposite
/// of what absorbing an external edit means.
/// </para>
/// </summary>
public sealed class WorkspaceSession : IAsyncDisposable
{
	private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
		new UnboundedChannelOptions { SingleReader = true });

	private readonly DiskSynchronizer _synchronizer = new();
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SolutionLoader _loader;
	private readonly WorkerOptions _options;
	private readonly ILogger<WorkspaceSession> _logger;
	private readonly Task _pump;

	private MSBuildWorkspace _workspace;
	private Solution _current;
	private long _revision;

	private WorkspaceSession(
		LoadResult load,
		SolutionLoader loader,
		WorkerOptions options,
		ILogger<WorkspaceSession> logger)
	{
		_workspace = load.Workspace;
		_current = load.Workspace.CurrentSolution;
		_loader = loader;
		_options = options;
		_logger = logger;
		_revision = 1;

		_synchronizer.Reset(_current, options.SolutionPath);
		_pump = Task.Run(PumpAsync);
	}

	/// <summary>The revision as of the last completed operation.</summary>
	public long Revision => Interlocked.Read(ref _revision);

	public static WorkspaceSession Create(
		LoadResult load,
		SolutionLoader loader,
		WorkerOptions options,
		ILogger<WorkspaceSession> logger) => new(load, loader, options, logger);

	/// <summary>
	/// Drains pending mutations, reconciles with disk, and returns the resulting snapshot. This is
	/// the only way to obtain a solution to read from.
	/// </summary>
	public Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) =>
		EnqueueAsync(ReconcileAsync, cancellationToken);

	/// <summary>
	/// Runs <paramref name="mutation"/> on the writer against a freshly reconciled snapshot. The
	/// new solution it returns becomes authoritative and the revision advances.
	/// </summary>
	public Task<T> MutateAsync<T>(
		Func<WorkspaceSnapshot, CancellationToken, Task<MutationResult<T>>> mutation,
		CancellationToken cancellationToken)
	{
		return EnqueueAsync(async token =>
		{
			var snapshot = await ReconcileAsync(token);
			var result = await mutation(snapshot, token);

			if (result.Solution is not null)
			{
				_current = result.Solution;
				Interlocked.Increment(ref _revision);
			}

			return result.Value;
		}, cancellationToken);
	}

	/// <summary>
	/// Brings the snapshot back in line with disk. Always runs on the writer.
	/// <para>
	/// A text-level sweep handles ordinary edits. A structural change -- a project file, a props
	/// file, or the solution itself -- cannot be patched into an existing snapshot at all, so the
	/// solution is reopened instead. Doing that here, inside the barrier, is what stops a caller
	/// from ever seeing a snapshot that predates a branch switch.
	/// </para>
	/// </summary>
	private async Task<WorkspaceSnapshot> ReconcileAsync(CancellationToken cancellationToken)
	{
		var sync = await _synchronizer.SyncAsync(_current, cancellationToken);
		var notices = new List<string>();

		if (sync.StructuralChange)
		{
			notices.Add("Project or solution files changed on disk; the solution was reloaded.");
			await ReloadAsync(cancellationToken);
		}
		else if (sync.AnythingChanged)
		{
			_current = sync.Solution;
			Interlocked.Increment(ref _revision);

			if (sync.ChangedCount > 0)
				notices.Add($"Absorbed {sync.ChangedCount} external file change(s).");
			if (sync.RemovedCount > 0)
				notices.Add($"{sync.RemovedCount} tracked document(s) no longer exist on disk.");
		}

		if (sync.Deferred.Count > 0)
		{
			notices.Add($"{sync.Deferred.Count} file(s) were being written and will be re-read on the next call: "
				+ string.Join(", ", sync.Deferred.Take(5).Select(Path.GetFileName)));
		}

		return new WorkspaceSnapshot
		{
			Solution = _current,
			Revision = Revision,
			Notices = notices,
		};
	}

	/// <summary>Reopens the solution from scratch, replacing the workspace and the tracking table.</summary>
	private async Task ReloadAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Reloading {SolutionPath}.", _options.SolutionPath);

		var previous = _workspace;
		var load = await _loader.LoadAsync(_options, cancellationToken);

		_workspace = load.Workspace;
		_current = load.Workspace.CurrentSolution;
		_synchronizer.Reset(_current, _options.SolutionPath);
		Interlocked.Increment(ref _revision);

		previous.Dispose();
	}

	/// <summary>
	/// The single writer. Work items run strictly in order, which is what lets a read barrier
	/// promise that every mutation queued before it has already been applied.
	/// </summary>
	private async Task PumpAsync()
	{
		await foreach (var item in _queue.Reader.ReadAllAsync())
		{
			if (_shutdown.IsCancellationRequested)
			{
				item.Cancel();
				continue;
			}

			await item.RunAsync(_shutdown.Token);
		}
	}

	private async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

		await _queue.Writer.WriteAsync(new WorkItem(async token =>
		{
			try
			{
				completion.TrySetResult(await work(token));
			}
			catch (OperationCanceledException)
			{
				completion.TrySetCanceled(token);
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		}, () => completion.TrySetCanceled()), cancellationToken);

		// The caller's token abandons the wait; it does not abandon the queued work, which must run
		// to completion or the writer's ordering guarantee breaks.
		return await completion.Task.WaitAsync(cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		_queue.Writer.TryComplete();
		await _shutdown.CancelAsync();

		try
		{
			await _pump;
		}
		catch (OperationCanceledException)
		{
			// Expected during shutdown.
		}

		_workspace.Dispose();
		_shutdown.Dispose();
	}

	private sealed record WorkItem(Func<CancellationToken, Task> RunAsync, Action Cancel);
}

/// <summary>
/// What a mutation produced: the value to return, and the new solution when the mutation actually
/// changed something. A null solution means the mutation was a no-op and the revision holds.
/// </summary>
public readonly record struct MutationResult<T>(T Value, Solution? Solution);
