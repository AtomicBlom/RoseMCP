using System.Collections.Concurrent;
using System.Xml;

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
	CallerPaths paths,
	ILoggerFactory loggerFactory,
	ILogger<WorkspaceManager> logger) : IAsyncDisposable
{
	/// <summary>
	/// The open workers, by solution path.
	/// <para>
	/// Concurrent because the readers and the writer are not the same caller and never were: the tray
	/// enumerates this on a two-second timer while a call on another thread starts or replaces a
	/// worker, and a plain Dictionary read against a concurrent write is documented to throw or to
	/// corrupt its table. The gate below is a different guarantee -- it makes the compound
	/// check-dispose-replace-start sequence atomic, which no dictionary can do.
	/// </para>
	/// </summary>
	private readonly ConcurrentDictionary<string, WorkspaceWorker> _workers = new(PathCasing.Comparer);

	/// <summary>
	/// MSBuild properties asked for at reload, per solution. Kept because they belong to the worker's
	/// command line, and a worker replaced after a crash would otherwise lose them.
	/// </summary>
	private readonly ConcurrentDictionary<string, WorkspaceBuildOverrides> _buildOverrides =
		new(PathCasing.Comparer);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly BrokerOptions _options = options.Value;

	/// <summary>Cancelled when the manager goes, which ends the eviction sweep before the gate it takes is disposed.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>The eviction sweep, once a worker has started with eviction on. Started under the gate, so once.</summary>
	private Task? _sweeping;

	private int _disposed;

	private static readonly Dictionary<string, object?> NoArguments = [];

	/// <summary>
	/// What every worker is doing. Owned here rather than injected because the manager is the only
	/// thing that starts, stops, and calls workers, so it is the only thing that could fill it in.
	/// </summary>
	public ActivityLog Activities { get; } = new();

	/// <summary>
	/// Every worker the broker holds, for status reporting and the tray UI: the running ones, and the
	/// stopped ones whose rows stay so a person can read why they stopped. Being in this list is not
	/// being open; <see cref="WorkspaceWorker.IsAlive"/> is.
	/// </summary>
	public IReadOnlyList<WorkspaceWorker> Workers => [.. _workers.Values];

	/// <summary>
	/// One row per worker, running or stopped, memory and in-flight work included. The same model backs
	/// the tray window and GET /admin/workspaces, so the UI can never show something the API disagrees
	/// with. A stopped row says so in <see cref="Contracts.WorkspaceSummary.Alive"/>.
	/// </summary>
	public IReadOnlyList<Contracts.WorkspaceSummary> Describe() => [.. Workers.Select(worker => worker.Describe())];

	/// <summary>
	/// Every workspace this broker holds a worker for, as <c>rose_workspace_list</c> answers. Read
	/// from the registry without the gate and without calling any worker, so a list made while a
	/// solution is loading answers at once, and listing never counts as using a workspace.
	/// </summary>
	public Contracts.WorkspaceList List()
	{
		var now = UtcNow;

		return new Contracts.WorkspaceList
		{
			Workspaces = [.. Workers
				.OrderBy(worker => worker.SolutionPath, PathCasing.Comparer)
				.Select(worker => worker.ListEntry(now))],
			IdleEvictionAfter = _options.IdleEvictionAfter,
		};
	}

	/// <summary>
	/// The worker for whichever workspace <paramref name="hints"/> resolves to, starting one if
	/// needed.
	/// </summary>
	public Task<WorkspaceWorker> GetOrStartAsync(WorkspaceHints hints, CancellationToken cancellationToken) =>
		GetOrStartResolvedAsync(WorkspaceFor(hints), cancellationToken);

	/// <summary>
	/// The worker for whichever workspace <paramref name="hints"/> resolves to, starting one if
	/// needed, held against eviction until the returned hold is disposed. Taken under the gate the
	/// eviction sweep decides under, for something that calls the worker directly rather than
	/// through <see cref="CallAsync{T}"/>. <paramref name="use"/> says whether that counts as use and
	/// restarts the idle clock.
	/// </summary>
	public Task<(WorkspaceWorker Worker, IDisposable Hold)> HoldAsync(
		WorkspaceHints hints,
		bool use,
		CancellationToken cancellationToken) =>
		HoldResolvedAsync(WorkspaceFor(hints), use, cancellationToken);

	/// <summary>
	/// The worker for a solution path already decided on.
	/// <para>
	/// A dead worker is replaced rather than reported. Workers die for ordinary reasons -- the
	/// solution was deleted and has come back, a hard reload killed one, memory ran out, the sweep
	/// evicted it -- and making the caller retry after each of those would be needless ceremony.
	/// </para>
	/// </summary>
	private async Task<WorkspaceWorker> GetOrStartResolvedAsync(
		string solutionPath,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			return await GetOrStartUnderGateAsync(solutionPath, cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// The worker for a solution path, held for a call until the hold is disposed. Taken under the
	/// gate the eviction sweep decides under, so the sweep either stops the worker before this finds
	/// it -- and this starts a fresh one -- or sees it held and leaves it. There is no moment between
	/// a caller being handed a worker and calling it in which the sweep can stop it.
	/// <para>
	/// <paramref name="use"/> says whether the call counts as use and restarts the idle clock. Only
	/// tool calls do; a status call holds the worker without keeping it warm.
	/// </para>
	/// </summary>
	private async Task<(WorkspaceWorker Worker, IDisposable Hold)> HoldResolvedAsync(
		string solutionPath,
		bool use,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var worker = await GetOrStartUnderGateAsync(solutionPath, cancellationToken);
			return (worker, worker.Hold(use));
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>Only call while holding <see cref="_gate"/>.</summary>
	private async Task<WorkspaceWorker> GetOrStartUnderGateAsync(string solutionPath, CancellationToken cancellationToken)
	{
		if (_workers.TryGetValue(solutionPath, out var existing))
		{
			if (existing.IsAlive) return existing;

			logger.LogInformation(
				"Replacing the worker for {SolutionPath}; it stopped with {Reason}.",
				solutionPath,
				existing.ExitReason);

			await existing.DisposeAsync();
			_workers.TryRemove(solutionPath, out _);
			Activities.Forget(solutionPath);
		}

		if (!File.Exists(solutionPath))
		{
			throw new InvalidOperationException($"The solution no longer exists at {solutionPath}.");
		}

		var worker = await StartAsync(solutionPath, cancellationToken);

		_workers[solutionPath] = worker;
		EnsureSweeping();

		return worker;
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
	/// <remarks>
	/// The constraint is the invariant. Attribution is applied by matching the result against
	/// <see cref="WorkspaceScopedResult"/> at run time, so an unconstrained call would compile,
	/// answer, and silently say nothing about where the answer came from -- and a tool returning
	/// something else is exactly the change nobody would think to test. Here it does not compile.
	/// </remarks>
	public async Task<T> CallAsync<T>(
		WorkspaceHints hints,
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		bool retryIfWorkerDied,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null)
		where T : Contracts.WorkspaceScopedResult
	{
		var (worker, hold) = await HoldResolvedAsync(WorkspaceFor(hints), use: true, cancellationToken);

		try
		{
			try
			{
				return Attribute(await worker.CallAsync<T>(tool, arguments, cancellationToken, progress), worker);
			}
			catch (WorkerUnavailableException) when (retryIfWorkerDied)
			{
				logger.LogInformation("Replacing the worker for {SolutionPath} and retrying {Tool}.", worker.SolutionPath, tool);

				// Let go of the dead one first: a hold on a worker nobody can call keeps nothing alive.
				hold.Dispose();

				// GetOrStart rather than Restart, because Restart closes whatever is registered for the
				// path rather than the instance that just died. Two callers on one dead worker and the
				// second closes the replacement the first is already loading a solution into, mid-load.
				// GetOrStart replaces only an instance that is not alive, which is exactly this case.
				var (replacement, replacementHold) = await HoldResolvedAsync(worker.SolutionPath, use: true, cancellationToken);

				using (replacementHold)
				{
					return Attribute(
						await replacement.CallAsync<T>(tool, arguments, cancellationToken, progress), replacement);
				}
			}
		}
		catch (InvalidOperationException exception) when (
			exception is not WorkerUnavailableException
			&& Elsewhere(hints, worker.SolutionPath) is { } elsewhere)
		{
			// The worker's refusal is true about its own solution and says nothing about the one the path
			// is in. Same type, so nothing further in decides differently for the sentence added to it.
			throw new InvalidOperationException($"{exception.Message} {elsewhere}", exception);
		}
		finally
		{
			hold.Dispose();
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
		where T : WorkspaceScopedResult
	{
		WorkspaceScopedResult scoped = result;

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

		return [.. overlaps.Select(overlap =>
		{
			// A stopped row is still registered, so being registered is not being open: an evicted
			// worker holds nothing, and calling it open would say the sibling has the new text loaded.
			var open = _workers.TryGetValue(overlap.SolutionPath, out var sibling) switch
			{
				true when sibling.IsAlive => "open",
				true => $"not open (its worker stopped: {sibling.ExitReason})",
				false => "not open",
			};

			return $"{Path.GetFileName(overlap.SolutionPath)} also compiles {overlap.SharedFileCount} of the "
				+ $"file(s) this changed, and is {open}. This ran against "
				+ $"{Path.GetFileName(worker.SolutionPath)} alone, so anything referencing those files from "
				+ "projects only the other solution contains was not updated.";
		})];
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

	/// <summary>
	/// Status for whichever workspace <paramref name="hints"/> resolves to, starting it if nothing
	/// has been started for it.
	/// <para>
	/// Held for the call, so the sweep cannot stop the worker between finding it and asking it, but
	/// not counted as use: a session polling status to see whether a workspace is healthy is watching
	/// it, and watching must not keep a workspace nobody is working in warm forever.
	/// </para>
	/// <para>
	/// A workspace whose worker has stopped -- evicted, crashed, stopped by the broker -- is answered
	/// from its stopped row, and nothing is started. Starting one would reload the solution and wipe
	/// the record of why it stopped, so a session checking status every so often would keep an
	/// evicted workspace warm and never be told it had been evicted. The next call that needs the
	/// workspace starts it, as it always has.
	/// </para>
	/// </summary>
	public async Task<Contracts.WorkspaceStatusReport> StatusAsync(
		WorkspaceHints hints,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue>? progress = null)
	{
		var solutionPath = WorkspaceFor(hints);

		WorkspaceWorker worker;
		IDisposable hold;

		await _gate.WaitAsync(cancellationToken);
		try
		{
			var stopped = _workers.TryGetValue(solutionPath, out var registered) && !registered.IsAlive;
			if (stopped) return Attribute(registered!.StoppedStatus(), registered);

			worker = await GetOrStartUnderGateAsync(solutionPath, cancellationToken);
			hold = worker.Hold(use: false);
		}
		finally
		{
			_gate.Release();
		}

		using (hold)
		{
			return await StatusOfAsync(worker, cancellationToken, progress);
		}
	}

	/// <summary>
	/// Stops a worker and forgets it. Reopening starts a fresh process.
	/// <para>
	/// Attributed here rather than by the caller, for the reason every other result is: the
	/// resolution that turned a hint into a solution happened here, and it is the answer a caller
	/// with several workspaces open needs back.
	/// </para>
	/// </summary>
	public async Task<Contracts.WorkspaceClosed> CloseAsync(WorkspaceHints hints, CancellationToken cancellationToken)
	{
		var solutionPath = WorkspaceFor(hints);

		return new Contracts.WorkspaceClosed
		{
			Workspace = solutionPath,
			WorkspaceKey = Solutions.WorkspaceKey.For(solutionPath),
			Closed = await CloseResolvedAsync(solutionPath, cancellationToken),
		};
	}

	private async Task<bool> CloseResolvedAsync(string solutionPath, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!_workers.TryRemove(solutionPath, out var worker)) return false;

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
	/// The caller names a workspace one of two ways: <c>workspace</c>, a path, or <c>workspaceKey</c>,
	/// the key a result carried. They are alternatives, so a call sending both is refused rather than
	/// having one of them win: a key and a path that disagree are a mistake nothing here can settle,
	/// and a pair that agrees says nothing either one does not.
	/// </para>
	/// <para>
	/// The set of loaded workspaces is never an answer in itself. A bare call answered from the single
	/// open worker would be answered from what some other session did earlier rather than from the
	/// question: a session in one repository could be answered, plausibly and silently, from another.
	/// The set is consulted only to look up a key the caller sent, which is the caller naming the
	/// workspace, and is otherwise named only in the failures, where it helps.
	/// </para>
	/// <para>
	/// The failures throw McpException rather than ArgumentException, and the difference is the whole
	/// point: the SDK turns an unrecognised exception into "An error occurred invoking
	/// 'rose_diagnostics'." and drops the message, so a caller that could have fixed the call itself
	/// is told nothing. Each of these knows what the caller should do next, and says so.
	/// </para>
	/// </summary>
	public string WorkspaceFor(WorkspaceHints hints)
	{
		var isNamedTwice = hints.Workspace is not null && hints.WorkspaceKey is not null;
		if (isNamedTwice)
		{
			throw new McpException(
				$"Both workspace ({hints.Workspace!.Value}) and workspaceKey ({hints.WorkspaceKey}) were given, and each "
					+ "names a workspace on its own. Send only one: workspaceKey to name a loaded workspace by the key "
					+ "a result carried, or workspace to name a solution, project or file by its path.");
		}

		// The caller named it. A name that resolves to nothing is theirs to hear about, so nothing
		// here is caught -- falling through to a guess would answer a different question than asked.
		if (hints.Workspace is { } named)
		{
			RefuseAKeySentAsAPath(named);
			return Resolved(named.Value);
		}

		// Named by the key a result carried, and strict for the same reason. Only a loaded workspace
		// can be found that way, since a key cannot be turned back into the path it was taken from.
		if (hints.WorkspaceKey is { } key) return ByKey(key, [.. _workers.Keys]);

		// Paths the call carries for its own reasons. The first that decides wins; an ambiguous one is
		// remembered rather than thrown, because a later hint may still settle it and, failing that,
		// an ambiguity about a path the caller actually named explains more than one about a directory.
		AmbiguousSolutionException? ambiguity = null;

		foreach (var (_, routed) in hints.Routable())
		{
			try
			{
				return Resolved(routed);
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

		var origin = paths.Origin;

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
	/// Refuses a workspace key sent as the <c>workspace</c> argument.
	/// <para>
	/// The two arguments sit side by side and the key is what a result hands back, so a caller will
	/// sometimes send it under the wrong name. Taken as a path it is measured from the session's
	/// directory, names nothing there, and resolution walks up from it to the session's own solution
	/// -- an answer from a workspace that may not be the one the key named, with nothing in it saying
	/// the argument was misread. A path that exists is honoured whatever its name looks like; only one
	/// naming nothing on disk whose last segment has the key's shape is refused, and it is named as the
	/// key of a loaded workspace where it is one.
	/// </para>
	/// </summary>
	private void RefuseAKeySentAsAPath(RootedPath named)
	{
		var exists = File.Exists(named.Value) || Directory.Exists(named.Value);
		var sent = Path.GetFileName(named.Value);
		var isShapedLikeAKey = !exists && Solutions.WorkspaceKey.HasShape(sent);
		if (!isShapedLikeAKey) return;

		var owner = _workers.Keys.FirstOrDefault(
			path => string.Equals(Solutions.WorkspaceKey.For(path), sent, StringComparison.OrdinalIgnoreCase));
		var whose = owner is null ? string.Empty : $" It is the key of {owner}, which is loaded.";

		throw new McpException(
			$"workspace was given {sent}, which names nothing on disk and is shaped like a workspace key.{whose} "
				+ "Send a key as workspaceKey, and workspace as the path of a solution, project or file.");
	}

	/// <summary>
	/// The loaded solution carrying <paramref name="key"/>, the way <see cref="Solutions.WorkspaceKey"/>
	/// derives it.
	/// <para>
	/// Only what is loaded can answer, because a key is a hash and cannot be turned back into the path
	/// it came from. That is enough for the caller the key exists for: one that read it off a result,
	/// which a loaded worker produced. A broker that has restarted since has forgotten it, which is a
	/// failure naming what is loaded and the argument that works regardless, not a guess.
	/// </para>
	/// <para>
	/// The hash is four bytes, so two loaded solutions can share a key, however rarely; that is refused
	/// with both paths rather than settled by whichever the dictionary yielded first. Matched without
	/// regard to case, since the hex half is never upper case and a solution name differing only in
	/// case already differs in its hash.
	/// </para>
	/// <para>
	/// Static and public so the matching can be tested without starting a worker for every solution it
	/// is asked to tell apart.
	/// </para>
	/// </summary>
	/// <param name="key">The key the caller sent.</param>
	/// <param name="loaded">The solution paths of the loaded workers.</param>
	/// <exception cref="McpException">No loaded solution carries the key, or more than one does.</exception>
	public static string ByKey(string key, IReadOnlyCollection<string> loaded)
	{
		var wanted = key.Trim();
		var matching = loaded
			.Where(path => string.Equals(Solutions.WorkspaceKey.For(path), wanted, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (matching.Length == 1) return matching[0];

		if (matching.Length > 1)
		{
			throw new McpException(
				$"The workspaceKey {wanted} belongs to {matching.Length} loaded workspaces, which happen to hash alike: "
					+ $"{string.Join(", ", matching)}. Pass workspace with the path of the one you mean instead.");
		}

		if (loaded.Count == 0)
		{
			throw new McpException(
				$"No loaded workspace has the workspaceKey {wanted}, and none is loaded: a key names a workspace only "
					+ "while the broker that issued it has it loaded, and this one has restarted or closed it since. "
					+ "Pass workspace with the solution's path instead, which loads it.");
		}

		var known = loaded.Select(path => $"{Solutions.WorkspaceKey.For(path)} ({path})");

		throw new McpException(
			$"No loaded workspace has the workspaceKey {wanted}. A key names a workspace only while it is loaded, and "
				+ $"the loaded ones are: {string.Join(", ", known)}. Pass one of those keys, or workspace with the "
				+ "solution's path, which loads it if it is not.");
	}

	/// <summary>
	/// What to add to a failure answered by <paramref name="answeredBy"/> when the path the call carries
	/// belongs to a different solution; null where it belongs to that one, or to nothing this can name.
	/// <para>
	/// A worker can only describe its own solution, so a path in another checkout comes back as a
	/// refusal that is true there and misleading here: "not inside any project's directory" about a
	/// file that sits inside a project of a solution open beside it. The caller reaches that state by
	/// naming the wrong workspace, or by a path whose own directory could not decide between several
	/// solutions so the session's directory answered instead, and either way the fix is the
	/// workspace argument -- which only this side knows to suggest, since only this side chose.
	/// </para>
	/// <para>
	/// Only the first path routing would have used is asked about, and the advice names a solution
	/// only where that solution compiles the path -- of several sharing a directory, only those that
	/// do, and nothing where none does -- so following it cannot bounce off the same refusal from the
	/// other side. Public for the reason <see cref="WorkspaceFor"/> is.
	/// </para>
	/// </summary>
	/// <param name="hints">What the call carried.</param>
	/// <param name="answeredBy">The solution whose worker answered.</param>
	public string? Elsewhere(WorkspaceHints hints, string answeredBy)
	{
		foreach (var (hint, routed) in hints.Routable())
		{
			try
			{
				if (SolutionResolver.Compiles(answeredBy, hint.Value)) return null;

				var owner = SolutionResolver.Resolve(routed);
				var isAnotherSolution = !PathCasing.Comparer.Equals(owner, answeredBy);
				var ownerCompilesIt = isAnotherSolution && SolutionResolver.Compiles(owner, hint.Value);
				if (!ownerCompilesIt) return null;

				return $"{hint.Value} is inside a project of {owner}, and {Path.GetFileName(answeredBy)} answered this "
					+ $"call. Pass the workspace argument (or solution) naming {owner}.";
			}
			catch (AmbiguousSolutionException ambiguity)
			{
				var compiling = ambiguity.Candidates
					.Where(candidate => !PathCasing.Comparer.Equals(candidate, answeredBy) && SolutionResolver.Compiles(candidate, hint.Value))
					.ToArray();
				if (compiling.Length == 0) return null;

				return $"{hint.Value} is inside no project of {Path.GetFileName(answeredBy)}, which answered this "
					+ $"call. {compiling.Length} solutions in {ambiguity.Directory} compile it: "
					+ $"{string.Join(", ", compiling.Select(Path.GetFileName))}. Pass the workspace argument (or solution) "
					+ $"naming the one you mean, or pin it for good with a \"solution\" entry in "
					+ $"{Path.Combine(ambiguity.Directory, "rosemcp.json")}.";
			}
			catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or XmlException)
			{
				// Nothing to load near it, or a solution file that cannot be read: the next path may say
				// more, and a caveat that cannot be worked out must not replace the failure it explains.
			}
		}

		return null;
	}

	/// <summary>
	/// Names the loaded workspaces when resolution has failed. They are no basis for choosing, but
	/// once choosing has failed they are the shortest route to a call that works, so each is given
	/// with the key that names it in far fewer characters than its path.
	/// </summary>
	private string OpenWorkspacesSuffix()
	{
		// Only workers that are serving. A stopped row stays registered for a while so a person can
		// read why it stopped, and calling it open would promise a warm answer where the next call
		// on it pays a full load.
		var open = _workers.Values.Where(worker => worker.IsAlive).Select(worker => worker.SolutionPath).ToList();

		if (open.Count == 0)
		{
			return ". Pass the workspace argument naming a solution, project, or any file inside one.";
		}

		var named = open.Select(path => $"{Solutions.WorkspaceKey.For(path)} ({path})");

		return ". Pass the workspace argument naming a solution, project, or any file inside one, or "
			+ $"workspaceKey naming one already open: {string.Join(", ", named)}.";
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

	/// <summary>Now, on the clock the workers' idle times are read from.</summary>
	private DateTime UtcNow => _options.TimeProvider.GetUtcNow().UtcDateTime;

	/// <summary>How an eviction is labelled in the activity log, beside "start worker" and "load solution".</summary>
	public const string EvictOperation = "evict worker";

	/// <summary>
	/// Starts the eviction sweep, when eviction is on and it is not already running. Only call while
	/// holding <see cref="_gate"/>, which is what makes the check and the start one step.
	/// <para>
	/// Started by the first worker rather than by the constructor, so a broker that never opens a
	/// solution -- every unit test that builds the registration, and a tray nobody has used yet -- runs
	/// no timer at all.
	/// </para>
	/// </summary>
	private void EnsureSweeping()
	{
		if (_sweeping is not null || _options.IdleEvictionAfter is not { } idleAfter) return;

		// Read here rather than inside the task, so the loop holds the token it was started with.
		var stopping = _stopping.Token;
		_sweeping = Task.Run(() => SweepLoopAsync(idleAfter, stopping));
	}

	/// <summary>
	/// Sweeps on a timer for as long as this manager lives, and stops when it is disposed.
	/// <para>
	/// One sweep failing does not end the loop: it has changed nothing it did not finish, and a
	/// broker that silently stopped evicting after one bad tick would collect workers for the rest of
	/// its life, which is the failure this exists to prevent.
	/// </para>
	/// </summary>
	private async Task SweepLoopAsync(TimeSpan idleAfter, CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(_options.EvictionSweepInterval, _options.TimeProvider);

		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				try
				{
					await SweepAsync(idleAfter, cancellationToken);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					logger.LogWarning(exception, "The eviction sweep failed; the next one is in {Interval}.", _options.EvictionSweepInterval);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// The manager is going away, and its workers with it.
		}
	}

	/// <summary>
	/// One pass over the registry. Deciding needs a file check per worker, so it is done outside the
	/// gate, and only a worker the decision would act on waits for it -- where it is decided again,
	/// because a call may have taken the worker in the meantime.
	/// </summary>
	private async Task SweepAsync(TimeSpan idleAfter, CancellationToken cancellationToken)
	{
		var now = UtcNow;

		foreach (var worker in Workers)
		{
			worker.ObserveSolution(File.Exists(worker.SolutionPath), now);

			var verdict = WorkerEviction.Decide(worker.EvictionFacts(), idleAfter, _options.SolutionGoneGrace, now);
			if (verdict == EvictionVerdict.Keep) continue;

			await EvictAsync(worker, idleAfter, cancellationToken);
		}
	}

	/// <summary>
	/// Acts on one worker the sweep picked, under the gate every call takes its worker under.
	/// <para>
	/// Everything is read again here. Holding the gate, nobody can be handed this worker, so a worker
	/// nobody holds, with nothing running, idle past the limit, is a worker no call is about to use --
	/// and one somebody took between the first look and this one is seen held and left alone. A worker
	/// already replaced by a fresh one is not the one in the registry any more, and is left too.
	/// </para>
	/// <para>
	/// An evicted worker stays registered, stopped, rather than being removed. Its row and its
	/// activity history are what tell a person -- in the tray, in <c>GET /admin/workspaces</c>, and in
	/// <c>rose_workspace_list</c> -- that it was evicted and why, and the next call replaces it exactly
	/// as it replaces a crashed one. The row goes once it has been stopped as long as the idle limit.
	/// </para>
	/// </summary>
	private async Task EvictAsync(WorkspaceWorker worker, TimeSpan idleAfter, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var stillRegistered = _workers.TryGetValue(worker.SolutionPath, out var current) && ReferenceEquals(current, worker);
			if (!stillRegistered) return;

			var now = UtcNow;
			var facts = worker.EvictionFacts();
			var verdict = WorkerEviction.Decide(facts, idleAfter, _options.SolutionGoneGrace, now);

			if (verdict == EvictionVerdict.Forget)
			{
				_workers.TryRemove(new KeyValuePair<string, WorkspaceWorker>(worker.SolutionPath, worker));
				await worker.DisposeAsync();
				Activities.Forget(worker.SolutionPath);

				logger.LogDebug(
					"Dropped the row for {SolutionPath}, stopped with {Reason} at {StoppedUtc}.",
					worker.SolutionPath,
					worker.ExitReason,
					worker.StoppedUtc);
				return;
			}

			var evicting = verdict is EvictionVerdict.EvictIdle or EvictionVerdict.EvictSolutionGone;
			if (!evicting) return;

			var reason = WorkerEviction.Explain(verdict, facts, idleAfter, now);

			// Marked before it is disposed, so the reason recorded is this one rather than the
			// StoppedByBroker that disposing would record.
			worker.MarkStopped(WorkerExitReason.Evicted, reason);
			Activities.Note(worker.SolutionPath, EvictOperation, reason);

			await worker.DisposeAsync();

			logger.LogInformation(
				"Evicted the worker for {SolutionPath} ({WorkspaceKey}), idle for {Idle}: {Reason}",
				worker.SolutionPath,
				worker.Key,
				WorkerEviction.Duration(now - facts.LastUsedUtc),
				reason);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		// Once: a second CancelAsync on a disposed source throws, and a host and its container can both
		// reach this.
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		// The sweep first, and awaited: it takes the gate disposed below, and acts on the workers
		// disposed below, so it has to have stopped before either goes.
		await _stopping.CancelAsync();

		if (_sweeping is { } sweeping) await sweeping;

		foreach (var worker in Workers)
		{
			await worker.DisposeAsync();
		}

		_workers.Clear();

		_gate.Dispose();
		_stopping.Dispose();
	}
}
