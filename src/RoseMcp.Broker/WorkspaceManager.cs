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

	private static readonly Dictionary<string, object?> NoArguments = [];

	/// <summary>
	/// What every worker is doing. Owned here rather than injected because the manager is the only
	/// thing that starts, stops, and calls workers, so it is the only thing that could fill it in.
	/// </summary>
	public ActivityLog Activities { get; } = new();

	/// <summary>Open workspaces, for status reporting and the tray UI.</summary>
	public IReadOnlyList<WorkspaceWorker> Workers => [.. _workers.Values];

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
				_workers.TryRemove(solutionPath, out _);
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
		var worker = await GetOrStartAsync(hints, cancellationToken);

		try
		{
			try
			{
				return Attribute(await worker.CallAsync<T>(tool, arguments, cancellationToken, progress), worker);
			}
			catch (WorkerUnavailableException) when (retryIfWorkerDied)
			{
				logger.LogInformation("Replacing the worker for {SolutionPath} and retrying {Tool}.", worker.SolutionPath, tool);

				// GetOrStart rather than Restart, because Restart closes whatever is registered for the
				// path rather than the instance that just died. Two callers on one dead worker and the
				// second closes the replacement the first is already loading a solution into, mid-load.
				// GetOrStart replaces only an instance that is not alive, which is exactly this case.
				var replacement = await GetOrStartResolvedAsync(worker.SolutionPath, cancellationToken);

				return Attribute(
					await replacement.CallAsync<T>(tool, arguments, cancellationToken, progress), replacement);
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
			var open = _workers.ContainsKey(overlap.SolutionPath) ? "open" : "not open";

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
		if (hints.Workspace is { } named) return Resolved(named.Value);

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
		var open = _workers.Keys;

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

	public async ValueTask DisposeAsync()
	{
		foreach (var worker in Workers)
		{
			await worker.DisposeAsync();
		}

		_workers.Clear();

		_gate.Dispose();
	}
}
