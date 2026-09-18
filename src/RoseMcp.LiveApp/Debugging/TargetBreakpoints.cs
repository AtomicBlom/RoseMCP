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
	/// Adds a tracepoint: a breakpoint that logs and auto-continues, never pausing the target. It binds
	/// immediately if its module is already loaded and otherwise when the module loads.
	/// </summary>
	internal LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit, string? condition)
	{
		if (logEveryNthHit is < 1) throw new ArgumentException("logEveryNthHit must be at least 1.");

		var binding = AddBinding(location, stopOnHit: false, logMessage, logEveryNthHit, autoContinueSeconds: null, condition);
		lock (target.Gate)
		{
			return BreakpointTable.DescribeTracepoint(binding);
		}
	}

	/// <summary>
	/// Adds a stopping breakpoint: on hit it holds the target and records the stop with its stack, then
	/// auto-continues after <paramref name="autoContinueSeconds"/> (default 30) so an unattended stop
	/// cannot wedge the app. Call <see cref="CorDebugSession.Continue"/> to resume sooner.
	/// </summary>
	internal LiveBreakpoint AddBreakpoint(string location, int? autoContinueSeconds, string? condition)
	{
		if (autoContinueSeconds is < 1) throw new ArgumentException("autoContinueSeconds must be at least 1.");

		var binding = AddBinding(location, stopOnHit: true, logMessage: null, logEveryNthHit: null, autoContinueSeconds, condition);
		lock (target.Gate)
		{
			return BreakpointTable.DescribeBreakpoint(binding);
		}
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

	private BreakpointBinding AddBinding(string location, bool stopOnHit, string? logMessage, int? logEveryNthHit, int? autoContinueSeconds, string? condition)
	{
		BreakpointBinding binding;
		lock (target.Gate)
		{
			binding = _breakpoints.Add(location, stopOnHit, logMessage, logEveryNthHit, autoContinueSeconds, condition);
		}

		BindAgainstLoadedModules();
		return binding;
	}

	internal bool Remove(string id)
	{
		lock (target.Gate)
		{
			return _breakpoints.Remove(id);
		}
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
