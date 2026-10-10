using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

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
	SharedWorkProgress sharedWork,

	ILoggerFactory loggerFactory,
	IHostApplicationLifetime lifetime,
	ILogger<WorkspaceHost> logger) : IHostedService, IAsyncDisposable
{
	private readonly CancellationTokenSource _shutdown = new();
	private Task<WorkspaceSession>? _start;
	private volatile WorkspaceStatusReport? _faulted;
	private int _disposed;

	private readonly List<AssemblyLoadFault> _assemblyLoadFaults = [];
	private readonly Lock _faultGate = new();

	/// <summary>
	/// The newest status call's description of a load, which a read from the same load trusts over the load's
	/// own: it is later, so a generator built since or a project fixed since is already accounted for.
	/// </summary>
	private volatile DescribedLoad? _described;

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
	public async Task<WorkspaceStatusReport> GetStatusAsync(CancellationToken cancellationToken, IWorkProgress? progress = null)
	{
		if (_faulted is not null) return _faulted;

		try
		{
			var session = await StartedAsync();
			var snapshot = await session.ReadAsync(cancellationToken);
			var load = session.Load;

			var report = await WorkspaceStatusReporter.DescribeAsync(
				snapshot.Solution,
				options.SolutionPath,
				load.Diagnostics,
				load.Restore,
				load.EvaluationFailures,
				snapshot.Revision,
				load.Seconds,
				cancellationToken,
				progress,
				session.Build,
				loader.AnalyzerLoader);

			// Kept for the reads, which say the workspace is degraded without paying for this description.
			_described = new DescribedLoad(load, report.DegradedReasons);

			// A reconciliation notice is something that happened, not a reason to distrust the answer:
			// "Absorbed 16 external file change(s)" is the server doing the job it exists for. These
			// were being appended to degradedReasons, which emptied the word -- every status call
			// after an edit reported one -- and left State contradicting its own list, since State had
			// already been computed from the reasons the reporter found. Staleness is the exception
			// that belongs there: a snapshot served while the solution file is missing is the last
			// good one rather than current truth, which is exactly what Degraded means.
			// The assembly faults belong to the process rather than to the load or the snapshot, so they are held
			// here and survive every reload.
			var reasons = new List<string>(report.DegradedReasons);
			if (WorkspaceStatusReporter.AssemblyLoadReason(AssemblyLoadFaults) is { } faults) reasons.Add(faults);
			if (snapshot.Stale) reasons.AddRange(snapshot.Notices);

			var notices = snapshot.Stale
				? report.Notices
				: (IReadOnlyList<string>)[.. report.Notices, .. snapshot.Notices];

			return report with
			{
				State = reasons.Count == 0 ? WorkspaceState.Loaded : WorkspaceState.Degraded,
				DegradedReasons = reasons,
				Notices = notices,
			};
		}
		catch (SolutionUnloadedException exception)
		{
			return _faulted = Unload(exception);
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
	/// <para>
	/// Where the workspace is degraded the snapshot's notices lead with saying so, which every read passes
	/// on and a batch says once. Here rather than in the broker, which stamps each result with the workspace
	/// that answered, because the broker knows only the last status a client happened to ask for: it cannot
	/// see a reload this process made on its own, or an assembly a tool failed to load a call ago, which is
	/// exactly when a read's clean answer is least to be trusted. Here every read passes, and the status and
	/// the mutations do not, since status lists the reasons itself and a write's verdict comes from the
	/// compile it ran.
	/// </para>
	/// </summary>
	public async Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken)
	{
		var session = await StartedAsync();
		var snapshot = await session.ReadAsync(cancellationToken);

		return snapshot.Noting(WorkspaceStatusReporter.DegradedNotice(ReasonsToDistrust(session.Load)));
	}

	public async Task<WorkspaceSession> SessionAsync() => await StartedAsync();

	/// <summary>
	/// The analyzer assemblies this worker's reads have found rebuilt since it loaded them, or none while the
	/// solution is still loading or failed to. Never waits for the load or for the writer.
	/// </summary>
	public IReadOnlyList<string> RebuiltAnalyzerPaths =>
		_start is { IsCompletedSuccessfully: true } started ? started.Result.RebuiltAnalyzerPaths : [];

	/// <summary>
	/// Remembers that a tool call failed to load an assembly, so every status from here on is
	/// <see cref="WorkspaceState.Degraded"/> and says why rather than leaving it to whoever next calls the same
	/// tool to find out. Kept here, for the life of the process, and never cleared by a reload: a reload replaces
	/// the workspace inside this process, and it is the process that cannot load them.
	/// </summary>
	public void RecordAssemblyLoadFault(AssemblyLoadFault fault)
	{
		logger.LogError(
			"{Tool} failed because this worker could not load {Assembly}: {Message} Every call reaching the same code will fail the same way until the worker is restarted.",
			fault.Tool,
			fault.Assembly,
			fault.Message);

		lock (_faultGate)
		{
			var known = _assemblyLoadFaults.Any(existing =>
				string.Equals(existing.Assembly, fault.Assembly, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(existing.Tool, fault.Tool, StringComparison.Ordinal));

			if (!known) _assemblyLoadFaults.Add(fault);
		}
	}

	/// <summary>Every assembly a tool call failed to load for this worker's own code, one entry per assembly and tool.</summary>
	public IReadOnlyList<AssemblyLoadFault> AssemblyLoadFaults
	{
		get
		{
			lock (_faultGate)
			{
				return [.. _assemblyLoadFaults];
			}
		}
	}

	/// <summary>
	/// Why a read from <paramref name="load"/> should not be trusted, as status would say it, without describing
	/// the solution again: the newest full description of that load -- a status call's where one was made since
	/// the load, the load's own otherwise -- and every assembly a tool call failed to load, which belongs to the
	/// process and is current to the last call.
	/// </summary>
	private IReadOnlyList<string> ReasonsToDistrust(LoadOutcome load)
	{
		var described = _described is { } last && ReferenceEquals(last.Load, load) ? last.Reasons : load.DegradedReasons;

		return WorkspaceStatusReporter.AssemblyLoadReason(AssemblyLoadFaults) is { } faults ? [.. described, faults] : described;
	}

	/// <summary>What a status call's description of one load called degraded, before the process's own faults are added.</summary>
	private sealed record DescribedLoad(LoadOutcome Load, IReadOnlyList<string> Reasons);

	private async Task<WorkspaceSession> StartSessionAsync()
	{
		using var loading = sharedWork.Begin($"Loading {Path.GetFileName(options.SolutionPath)}");

		var load = await loader.LoadAsync(options, _shutdown.Token, sharedWork);
		return WorkspaceSession.Create(
			load,
			loader,
			options,
			loggerFactory.CreateLogger<WorkspaceSession>(),
			loggerFactory.CreateLogger<SolutionWatcher>(),
			sharedWork);
	}

	private Task<WorkspaceSession> StartedAsync() =>
		_start ?? throw new InvalidOperationException("The workspace host has not been started.");

	/// <summary>
	/// The solution is gone for good. Reporting it is only half the job: a worker holding a
	/// solution that no longer exists is dead weight, so the process comes down with it. The
	/// broker sees the exit and deregisters the workspace.
	/// </summary>
	private WorkspaceStatusReport Unload(SolutionUnloadedException exception)
	{
		logger.LogWarning("{Message} Shutting this worker down.", exception.Message);
		lifetime.StopApplication();

		return new WorkspaceStatusReport
		{
			SolutionPath = options.SolutionPath,
			State = WorkspaceState.Unloaded,
			Revision = 0,
			Projects = [],
			LoadDiagnostics = [],
			DegradedReasons = [exception.Message],
		};
	}

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
		// Registered as both a singleton and a hosted service, so this runs twice: once via
		// StopAsync and again when the container disposes the singleton. Without this guard the
		// second pass throws on the disposed CancellationTokenSource and the worker exits
		// non-zero, which the broker would read as a crash.
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
