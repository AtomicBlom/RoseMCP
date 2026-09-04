using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol;

using RoseMcp.Contracts;
using RoseMcp.Solutions;

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
	private readonly Dictionary<string, WorkspaceWorker> _workers = new(PathCasing.Comparer);

	/// <summary>
	/// MSBuild properties asked for at reload, per solution. Kept because they belong to the worker's
	/// command line, and a worker replaced after a crash would otherwise lose them.
	/// </summary>
	private readonly Dictionary<string, WorkspaceBuildOverrides> _buildOverrides =
		new(PathCasing.Comparer);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly BrokerOptions _options = options.Value;

	private static readonly Dictionary<string, object?> NoArguments = [];

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
	/// The worker for whichever workspace <paramref name="hints"/> resolves to, starting one if
	/// needed.
	/// </summary>
	public Task<WorkspaceWorker> GetOrStartAsync(WorkspaceHints hints, CancellationToken cancellationToken) =>
		GetOrStartResolvedAsync(WorkspaceFor(hints), cancellationToken);

	/// <summary>
	/// The worker for a solution path already decided on.
	/// <para>
	/// A dead worker is replaced rather than reported. Workers die for ordinary reasons -- the
	/// solution was deleted and has come back, a hard reload killed one, memory ran out -- and
	/// making the caller retry after each of those would be needless ceremony.
	/// </para>
	/// </summary>
	private async Task<WorkspaceWorker> GetOrStartResolvedAsync(
		string solutionPath,
		CancellationToken cancellationToken)
	{
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
		WorkspaceHints hints,
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		bool retryIfWorkerDied,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null)
	{
		var worker = await GetOrStartAsync(hints, cancellationToken);

		try
		{
			return Attribute(await worker.CallAsync<T>(tool, arguments, cancellationToken, progress), worker);
		}
		catch (WorkerUnavailableException) when (retryIfWorkerDied)
		{
			logger.LogInformation("Restarting the worker for {SolutionPath} and retrying {Tool}.", worker.SolutionPath, tool);

			var replacement = await RestartResolvedAsync(worker.SolutionPath, cancellationToken);

			return Attribute(
				await replacement.CallAsync<T>(tool, arguments, cancellationToken, progress), replacement);
		}
	}

	/// <summary>
	/// Stamps a result with the workspace that produced it.
	/// <para>
	/// Here rather than in the worker because the worker was told which solution to own and never
	/// chose it -- the choice is the thing worth reporting, and this is where it was made. One place
	/// also means a tool added later is attributed without anyone remembering to do it.
	/// </para>
	/// </summary>
	private T Attribute<T>(T result, WorkspaceWorker worker)
	{
		if (result is not WorkspaceScopedResult scoped) return result;

		var attributed = scoped with { Workspace = worker.SolutionPath, WorkspaceKey = worker.Key };

		return (T)(object)(attributed is WorkspaceMutationResult mutation
			? mutation with { Notices = [.. mutation.Notices, .. SharedFileNotices(mutation, worker)] }
			: attributed);
	}

	/// <summary>
	/// Warns when a change touched files another solution beside this one also compiles.
	/// <para>
	/// Reported rather than acted on. Making the change complete across solutions means loading them
	/// all and merging the edits, which is a much larger thing than a warning and not always even
	/// well defined -- two solutions can build the same project under configurations that have no
	/// setting in common. Saying which sibling is affected costs a file read per candidate and turns
	/// a silent half-change into one the caller can finish deliberately.
	/// </para>
	/// </summary>
	private IReadOnlyList<string> SharedFileNotices(WorkspaceMutationResult mutation, WorkspaceWorker worker)
	{
		IReadOnlyList<SolutionOverlap> overlaps;
		try
		{
			overlaps = SolutionResolver.SiblingsSharing(worker.SolutionPath, mutation.ChangedFiles);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A caveat that cannot be computed must not fail the change that has already happened.
			logger.LogDebug(exception, "Could not check for solutions sharing {SolutionPath}.", worker.SolutionPath);
			return [];
		}

		if (overlaps.Count == 0) return [];

		lock (_workers)
		{
			return [.. overlaps.Select(overlap =>
			{
				var open = _workers.ContainsKey(overlap.SolutionPath) ? "open" : "not open";

				return $"{Path.GetFileName(overlap.SolutionPath)} also compiles {overlap.SharedFileCount} of the "
					+ $"file(s) this changed, and is {open}. This ran against "
					+ $"{Path.GetFileName(worker.SolutionPath)} alone, so anything referencing those files from "
					+ "projects only the other solution contains was not updated.";
			})];
		}
	}

	/// <summary>
	/// Status for a worker already in hand, attributed like every other result.
	/// <para>
	/// The lifecycle tools have their worker before they ask it anything -- opening and reloading are
	/// about a particular process, not about routing a call -- so they cannot go through
	/// <see cref="CallAsync{T}"/>. This is the same attribution step, so they cannot drift from it:
	/// answering "which workspace is this?" without naming the workspace would be an odd thing for
	/// status of all tools to do.
	/// </para>
	/// </summary>
	public async Task<Contracts.WorkspaceStatusReport> StatusOfAsync(
		WorkspaceWorker worker,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null) =>
		Attribute(
			await worker.CallAsync<Contracts.WorkspaceStatusReport>(
				Contracts.ToolNames.WorkspaceStatus, NoArguments, cancellationToken, progress),
			worker);

	/// <summary>Stops a worker and forgets it. Reopening starts a fresh process.</summary>
	public Task<bool> CloseAsync(WorkspaceHints hints, CancellationToken cancellationToken) =>
		CloseResolvedAsync(WorkspaceFor(hints), cancellationToken);

	private async Task<bool> CloseResolvedAsync(string solutionPath, CancellationToken cancellationToken)
	{
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
	public Task<WorkspaceWorker> RestartAsync(
		WorkspaceHints hints,
		CancellationToken cancellationToken,
		WorkspaceBuildOverrides? build = null) =>
		RestartResolvedAsync(WorkspaceFor(hints), cancellationToken, build);

	private async Task<WorkspaceWorker> RestartResolvedAsync(
		string solutionPath,
		CancellationToken cancellationToken,
		WorkspaceBuildOverrides? build = null)
	{
		// Remembered rather than applied once, so a worker that dies and is replaced later comes back
		// under the properties that were asked for rather than silently reverting.
		if (build is not null) _buildOverrides[solutionPath] = build;

		await CloseResolvedAsync(solutionPath, cancellationToken);

		return await GetOrStartResolvedAsync(solutionPath, cancellationToken);
	}

	/// <summary>
	/// Works out which workspace a call means. The one place that decides, and the order is the
	/// whole design.
	/// <para>
	/// Public because it answers a question worth asking without paying for it -- deciding is a few
	/// file reads, where acting on the decision is a design-time build -- and because a routing rule
	/// that can only be observed by running it is a routing rule nobody can test.
	/// </para>
	/// <para>
	/// Inputs are tried by how much they know about the question actually asked. What the caller
	/// named beats what the call implies, because they said it. What the call implies beats the
	/// session's directory, because a path in the arguments is evidence about this question whereas a
	/// directory is only where the asking happens to be from. And the session's directory is the last
	/// word, because a bare call still has to work -- a tool that demands a setup call first is a
	/// tool that loses to grep before it is ever tried.
	/// </para>
	/// <para>
	/// What is deliberately absent is the set of loaded workspaces. This used to answer a bare call
	/// from the single open worker, which is not a fact about the question at all but about what some
	/// other session did earlier: a session in one repository could be answered, plausibly and
	/// silently, from another. It is only ever named in the failure below, where it helps.
	/// </para>
	/// <para>
	/// Both failures throw McpException rather than ArgumentException, and the difference is the whole
	/// point: the SDK turns an unrecognised exception into "An error occurred invoking
	/// 'rose_diagnostics'." and drops the message, so a caller that could have fixed the call itself
	/// is told nothing. Both of these know what the caller should do next, and both say so.
	/// </para>
	/// </summary>
	public string WorkspaceFor(WorkspaceHints hints)
	{
		// The caller named it. A name that resolves to nothing is theirs to hear about, so nothing
		// here is caught -- falling through to a guess would answer a different question than asked.
		if (!string.IsNullOrWhiteSpace(hints.Workspace)) return Resolved(hints.Workspace);

		// Paths the call carries for its own reasons. The first that decides wins; an ambiguous one is
		// remembered rather than thrown, because a later hint may still settle it and, failing that,
		// an ambiguity about a path the caller actually named explains more than one about a directory.
		AmbiguousSolutionException? ambiguity = null;

		foreach (var path in hints.Paths)
		{
			if (string.IsNullOrWhiteSpace(path)) continue;

			// A hint need not be a path at all: diagnostics' target is a project name under project
			// scope. Resolving that as a path makes it relative to the process working directory and
			// answers from whichever solution is sitting there, which is worse than not trying.
			if (!File.Exists(path) && !Directory.Exists(path)) continue;

			try
			{
				return Resolved(path);
			}
			catch (AmbiguousSolutionException exception)
			{
				ambiguity ??= exception;
			}
			catch (ArgumentException)
			{
				// Nothing to load near it. The next hint, or the session's directory, may do better.
			}
		}

		var origin = CallOrigin.Directory ?? _options.DefaultWorkspaceRoot;

		try
		{
			return Resolved(origin);
		}
		catch (AmbiguousSolutionException) when (ambiguity is not null)
		{
			throw ambiguity;
		}
		catch (ArgumentException exception)
		{
			if (ambiguity is not null) throw ambiguity;

			throw new McpException(
				$"No solution or project was found near {origin}{OpenWorkspacesSuffix()}", exception);
		}
	}

	/// <summary>
	/// Names the loaded workspaces when resolution has failed. They are no basis for choosing, but
	/// once choosing has failed they are the shortest route to a call that works -- each result
	/// carries the key needed to name one.
	/// </summary>
	private string OpenWorkspacesSuffix()
	{
		lock (_workers)
		{
			if (_workers.Count == 0)
			{
				return ". Pass the workspace argument naming a solution, project, or any file inside one.";
			}

			return ". Pass the workspace argument naming a solution, project, or any file inside one. "
				+ $"Already open: {string.Join(", ", _workers.Keys)}.";
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
