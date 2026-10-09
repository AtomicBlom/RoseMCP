using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Every breakpoint and tracepoint asked for on a target, and the module metadata that says where
/// each one goes.
/// <para>
/// One object because the two are answerable only together. A binding names a place in source; what
/// turns that into an address is a module's metadata and its PDB, and which modules exist is a
/// question only the live process answers. Binding, explaining why something did not bind, and
/// searching for somewhere to put the next one all read both halves, so splitting them would mean
/// two objects locked in step.
/// </para>
/// <para>
/// The reads that matter most happen off the debuggee. Which modules are loaded is asked once and
/// remembered; everything after that is metadata on disk, so a name search answers while the target
/// is running -- and while it is wedged, which is when somebody most wants a breakpoint.
/// </para>
/// </summary>
internal sealed class TargetBreakpoints(DebuggedTarget target, DebugEventBuffer buffer, ILogger logger)
{
	/// <summary>
	/// Every module file the target has loaded, and what their metadata and symbols say.
	/// <para>
	/// Kept rather than asked for each time, because asking means synchronizing the target and a
	/// search runs on every keystroke of an autocomplete. What is inside a module is on disk, so once
	/// the path is known nothing else about the answer needs the debuggee at all.
	/// </para>
	/// </summary>
	private readonly TargetSymbols _symbols = new(logger);

	/// <summary>
	/// Every breakpoint and tracepoint asked for, bound or waiting for the module that would carry it.
	/// </summary>
	private readonly BreakpointTable _breakpoints = new(buffer, logger);

	/// <summary>
	/// Adds tracepoints: breakpoints that log and auto-continue, never pausing the target. Each binds
	/// immediately if its module is already loaded and otherwise when the module loads.
	/// <para>
	/// A request that cannot be added -- a location, message or condition that does not parse, a
	/// hit filter below one -- is refused in its own entry and the rest are added regardless, because
	/// the requests are independent: a caller instrumenting a path should get the four that parse and
	/// be told about the fifth, not have to send the four again.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">Nothing was asked for.</exception>
	internal LiveTracepointBatch AddTracepoints(IReadOnlyList<AddTracepointRequest> requests)
	{
		var added = AddBindings(
			"tracepoints",
			requests,
			static request => request?.Location,
			(table, request) =>
			{
				if (request.LogEveryNthHit is < 1) throw new ArgumentException("logEveryNthHit must be at least 1.");

				return table.Add(request.Location, stopOnHit: false, request.LogMessage, request.LogEveryNthHit, autoContinueSeconds: null, request.Condition);
			});

		lock (target.Gate)
		{
			var results = added
				.Select(entry => entry.Binding is { } binding
					? new LiveTracepointOutcome
					{
						Location = entry.Location,
						Status = Recorded("added", binding),
						Tracepoint = BreakpointTable.DescribeTracepoint(binding),
					}
					: new LiveTracepointOutcome { Location = entry.Location, Status = $"refused: {entry.Refusal}" })
				.ToList();

			return new LiveTracepointBatch
			{
				Added = results.Count(outcome => outcome.Tracepoint is not null),
				Results = results,
				Notes = Repeated(added, "tracepoint"),
			};
		}
	}

	/// <summary>
	/// Sets stopping breakpoints: on hit each holds the target and records the stop with its stack, then
	/// auto-continues after its <see cref="SetBreakpointRequest.AutoContinueSeconds"/> (default 30) so an
	/// unattended stop cannot wedge the app. Call <see cref="CorDebugSession.Continue"/> to resume sooner.
	/// A request that cannot be set is refused in its own entry, as <see cref="AddTracepoints"/> does.
	/// </summary>
	/// <exception cref="ArgumentException">Nothing was asked for.</exception>
	internal LiveBreakpointBatch AddBreakpoints(IReadOnlyList<SetBreakpointRequest> requests)
	{
		var added = AddBindings(
			"breakpoints",
			requests,
			static request => request?.Location,
			(table, request) =>
			{
				if (request.AutoContinueSeconds is < 1) throw new ArgumentException("autoContinueSeconds must be at least 1.");

				return table.Add(request.Location, stopOnHit: true, logMessage: null, logEveryNthHit: null, request.AutoContinueSeconds, request.Condition);
			});

		lock (target.Gate)
		{
			var results = added
				.Select(entry => entry.Binding is { } binding
					? new LiveBreakpointOutcome
					{
						Location = entry.Location,
						Status = Recorded("set", binding),
						Breakpoint = BreakpointTable.DescribeBreakpoint(binding),
					}
					: new LiveBreakpointOutcome { Location = entry.Location, Status = $"refused: {entry.Refusal}" })
				.ToList();

			return new LiveBreakpointBatch
			{
				Set = results.Count(outcome => outcome.Breakpoint is not null),
				Results = results,
				Notes = Repeated(added, "breakpoint"),
			};
		}
	}

	/// <summary>
	/// The status of an entry that was recorded: the verb alone when it bound; <c>not bound yet</c>
	/// and the reason when it is waiting for a module to load, which a caller can leave alone; and
	/// <c>will not bind</c> and the reason when the module that would carry it is loaded and cannot,
	/// which waiting will not cure. Saying both the same way would send a caller with a misspelled
	/// method off to wait for a load that has already happened.
	/// </summary>
	private static string Recorded(string verb, BreakpointBinding binding)
	{
		if (binding.Bound) return verb;

		var hasReason = binding.Detail is not (null or BreakpointTable.NotBoundYet);
		var reason = hasReason ? $": {binding.Detail}" : string.Empty;

		return binding.WillNotBind ? $"{verb}, will not bind{reason}" : $"{verb}, not bound yet{reason}";
	}

	internal IReadOnlyList<LiveTracepoint> ListTracepoints()
	{
		lock (target.Gate)
		{
			return _breakpoints.Tracepoints();
		}
	}

	internal IReadOnlyList<LiveBreakpoint> ListBreakpoints()
	{
		lock (target.Gate)
		{
			return _breakpoints.Breakpoints();
		}
	}

	/// <summary>
	/// Finds methods by name across the target's loaded modules, best first, for choosing somewhere
	/// to put a breakpoint without an IDE to browse.
	/// <para>
	/// The reading happens outside the session's lock and outside the debuggee. Which modules are
	/// loaded is the only thing the target is asked, and that is asked once; everything after it is
	/// metadata on disk. So this answers while the target is running, which is what an autocomplete
	/// needs -- and it answers while the target is wedged, which is when somebody most wants to set a
	/// breakpoint.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The limit is below one.</exception>
	internal LiveMethodMatches SearchMethods(string? query, int limit)
	{
		// Before the module paths are asked for, because asking can stop the target to walk them and
		// a limit that cannot be honoured must not cost the debuggee a synchronization.
		if (limit < 1) throw new ArgumentException("limit must be at least 1.", nameof(limit));

		return TargetSymbols.Search(ModulePaths(), query, limit);
	}

	/// <summary>
	/// A method's source and every position inside it a breakpoint can be set at.
	/// <para>
	/// The positions cover the lambdas, local functions and state machines written inside the method
	/// as well as the method itself, because that is where the instructions for those lines actually
	/// live. Each carries the location that breaks there, so picking a line inside a lambda produces
	/// a breakpoint in the lambda without anybody having to know its name.
	/// </para>
	/// <para>
	/// Every way this can come up short -- no such module, no such method, no symbols, symbols from
	/// another build, a source file this machine never had -- is a sentence and an empty listing
	/// rather than a refusal, because a breakpoint at the method's first instruction is still
	/// available in all of them.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The location does not parse.</exception>
	internal LiveMethodSource ReadMethodSource(string location) => TargetSymbols.ReadSource(ModulePaths(), location);

	/// <summary>
	/// Records every request that parses, then binds the lot against the loaded modules in one pass.
	/// <para>
	/// One pass rather than one per request because binding stops the whole target to walk its
	/// modules: six locations asked for together cost the debuggee one stop, not six.
	/// </para>
	/// <para>
	/// Only <see cref="ArgumentException"/> is a request's own refusal, since that is what a location,
	/// message or condition that does not parse throws. Anything else is a fault in the session rather
	/// than in what was asked, and fails the call as it would have for a single request.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">Nothing was asked for.</exception>
	private List<Requested> AddBindings<TRequest>(
		string argument,
		IReadOnlyList<TRequest?> requests,
		Func<TRequest?, string?> locationOf,
		Func<BreakpointTable, TRequest, BreakpointBinding> add)
		where TRequest : class
	{
		RequireSome(requests.Count, argument, "location");

		var added = new List<Requested>(requests.Count);
		lock (target.Gate)
		{
			foreach (var request in requests)
			{
				var location = locationOf(request) ?? string.Empty;
				if (request is null)
				{
					added.Add(new Requested(location, null, "an entry was empty; each needs a location"));
					continue;
				}

				try
				{
					added.Add(new Requested(location, add(_breakpoints, request), null));
				}
				catch (ArgumentException exception)
				{
					added.Add(new Requested(location, null, Reason(exception)));
				}
			}
		}

		if (added.Exists(entry => entry.Binding is not null)) BindAgainstLoadedModules();

		return added;
	}

	/// <summary>One request and what recording it produced: the binding, or why there is none.</summary>
	private sealed record Requested(string Location, BreakpointBinding? Binding, string? Refusal);

	/// <summary>
	/// A refusal's reason without the framework's <c>(Parameter 'spec')</c>, which names a parameter of a
	/// parser the caller never called rather than anything the caller sent. Taken out the way every MCP
	/// boundary takes it out, since this reason reaches the caller in an entry's status rather than
	/// through a boundary.
	/// </summary>
	private static string Reason(ArgumentException exception) => ToolArgumentShape.WithoutParameterNames(exception.Message);

	/// <summary>
	/// A note for each location asked for more than once in one call. Every copy is added -- two
	/// tracepoints on one method with different messages is a thing somebody can mean -- but the same
	/// location twice is more often a slip, and every copy logs or stops on every hit.
	/// </summary>
	private static IReadOnlyList<string> Repeated(IEnumerable<Requested> added, string kind) =>
	[
		.. added
			.Where(entry => entry.Binding is not null)
			.GroupBy(entry => entry.Location, StringComparer.Ordinal)
			.Where(group => group.Count() > 1)
			.Select(group => $"{group.Key} was asked for {group.Count()} times; each is its own {kind}, with its own id, and each acts on every hit."),
	];

	/// <summary>
	/// Removes tracepoints by id, each id's outcome its own entry, and returns the set left. An id the
	/// session does not hold is <c>not found</c> and a breakpoint's id is refused, so neither stops the
	/// rest; a caller cleaning up after a path is finished with should not have to know which of its
	/// ids are already gone.
	/// </summary>
	/// <exception cref="ArgumentException">No id was given.</exception>
	internal LiveTracepointRemoval RemoveTracepoints(IReadOnlyList<string?> ids)
	{
		lock (target.Gate)
		{
			var results = RemoveAll(ids, stopOnHit: false);

			return new LiveTracepointRemoval
			{
				Removed = CountRemoved(results),
				Results = results,
				Tracepoints = _breakpoints.Tracepoints(),
			};
		}
	}

	/// <summary>
	/// Removes stopping breakpoints by id, as <see cref="RemoveTracepoints"/> does tracepoints.
	/// Removing the one the target is held at does not resume it.
	/// </summary>
	/// <exception cref="ArgumentException">No id was given.</exception>
	internal LiveBreakpointRemoval RemoveBreakpoints(IReadOnlyList<string?> ids)
	{
		lock (target.Gate)
		{
			var results = RemoveAll(ids, stopOnHit: true);

			return new LiveBreakpointRemoval
			{
				Removed = CountRemoved(results),
				Results = results,
				Breakpoints = _breakpoints.Breakpoints(),
			};
		}
	}

	/// <summary>
	/// What a removal answers for a host with no target attached, where nothing is held: every id is
	/// not found. Refused when empty for the same reason a removal with a target is.
	/// </summary>
	/// <exception cref="ArgumentException">No id was given.</exception>
	internal static IReadOnlyList<LiveRemovalOutcome> NoneHeld(string argument, IReadOnlyList<string?> ids)
	{
		RequireSome(ids.Count, argument, "id");

		return [.. ids.Select(id => new LiveRemovalOutcome { Id = id ?? string.Empty, Status = NotFound })];
	}

	/// <summary>The status of an id the session does not hold.</summary>
	private const string NotFound = "not found";

	/// <summary>The status of an id whose binding is gone.</summary>
	private const string RemovedStatus = "removed";

	/// <summary>
	/// Removes each id that names a binding of the kind asked for. Called with the gate held.
	/// <para>
	/// The kind is checked because the two removals share one table: without it, a breakpoint's id
	/// given to the tracepoint removal takes the breakpoint away and answers with the tracepoint list,
	/// where nothing shows it has gone.
	/// </para>
	/// </summary>
	private List<LiveRemovalOutcome> RemoveAll(IReadOnlyList<string?> ids, bool stopOnHit)
	{
		RequireSome(ids.Count, stopOnHit ? "breakpointIds" : "tracepointIds", "id");

		var results = new List<LiveRemovalOutcome>(ids.Count);
		foreach (var id in ids)
		{
			results.Add(new LiveRemovalOutcome { Id = id ?? string.Empty, Status = RemoveOne(id, stopOnHit) });
		}

		return results;
	}

	private string RemoveOne(string? id, bool stopOnHit)
	{
		if (id is null || _breakpoints.StopsOnHit(id) is not { } stops) return NotFound;

		if (stops != stopOnHit)
		{
			return stops
				? $"refused: {id} is a stopping breakpoint, which {ToolNames.DebugRemoveBreakpoint} removes"
				: $"refused: {id} is a tracepoint, which {ToolNames.DebugRemoveTracepoint} removes";
		}

		return _breakpoints.Remove(id) ? RemovedStatus : NotFound;
	}

	private static int CountRemoved(IEnumerable<LiveRemovalOutcome> results) =>
		results.Count(outcome => outcome.Status == RemovedStatus);

	/// <summary>
	/// Refuses a call that names nothing. An empty list is a mistake in how the call was put
	/// together, and an answer with no entries in it would read as a call that worked.
	/// </summary>
	/// <exception cref="ArgumentException">The count is zero.</exception>
	private static void RequireSome(int count, string argument, string what)
	{
		if (count == 0) throw new ArgumentException($"{argument} is empty: nothing was asked for. Give at least one {what}.");
	}

	/// <summary>
	/// Binds any unbound bindings against modules already loaded when the binding was added. It
	/// async-breaks the target to a synchronized state to enumerate its modules, then resumes it; a
	/// binding whose module has not loaded yet stays unbound and binds later on the load callback.
	/// </summary>
	private void BindAgainstLoadedModules()
	{
		lock (target.Gate)
		{
			if (!target.TryLive(out var process)) return;
			if (_breakpoints.AllBound) return;

			var stopped = false;
			try
			{
				process.Stop(0);
				stopped = true;

				var loaded = new List<CorDebugModule>();
				foreach (var module in TargetSymbols.EnumerateModules(process))
				{
					_symbols.Remember(module);
					loaded.Add(module);
				}

				// This walk is the one a name search would otherwise have to take for itself.
				_symbols.MarkWalked();

				_breakpoints.BindAmongLoaded(loaded);
				_breakpoints.ExplainUnbound(_symbols.Paths);
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Binding against loaded modules failed.");
			}
			finally
			{
				if (stopped)
				{
					try
					{
						process.Continue(fIsOutOfBand: false);
					}
					catch (Exception exception)
					{
						logger.LogDebug(exception, "Continue after bind failed.");
					}
				}
			}
		}
	}

	/// <summary>
	/// The target's loaded module files, walking the ones that predate this session's attach the
	/// first time anybody asks. The walk is the only part that needs the gate or the debuggee; what
	/// a caller does with the paths afterwards is reading off disk.
	/// </summary>
	private IReadOnlyList<string> ModulePaths()
	{
		lock (target.Gate)
		{
			if (!_symbols.Walked && target.TryLive(out var live)) _symbols.Walk(live);

			return _symbols.Paths;
		}
	}

	/// <summary>
	/// Takes in a module as it loads: notes its file, and binds anything waiting for it. Called from a
	/// stopped callback.
	/// </summary>
	internal void BindModule(CorDebugModule module)
	{
		lock (target.Gate)
		{
			// Remembered before anything returns early, because the list of modules is wanted by a
			// name search whether or not anything is waiting to bind.
			_symbols.Remember(module);
			_breakpoints.BindNewModule(module, _symbols.Paths);
		}
	}
	/// <summary>
	/// Gives up every bound breakpoint so a detach can proceed. Called with the gate held and the
	/// process stopped.
	/// </summary>
	internal void ReleaseForDetach() => _breakpoints.ReleaseForDetach();

	/// <summary>
	/// The binding a hit belongs to, and which hit of it this is. Null when nothing claims it, which
	/// is what a breakpoint set by something other than this session looks like. Called with the gate
	/// held, from the callback that announced the hit.
	/// </summary>
	internal (BreakpointBinding? Binding, long Ordinal) Match(CorDebugFunctionBreakpoint? breakpoint) =>
		_breakpoints.Match(breakpoint);
}
