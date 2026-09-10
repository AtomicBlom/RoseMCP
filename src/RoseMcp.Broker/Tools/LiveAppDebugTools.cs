using System.ComponentModel;
using System.Diagnostics;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// The agent-facing debugging surface. Each tool drives a per-target live-app session the broker
/// supervises, the debugging counterpart to the per-solution workspace tools: attach or launch, watch
/// what the target throws and logs, set tracepoints and breakpoints, step, evaluate a field chain in a
/// stopped frame, read and edit the running visual tree, and detach.
/// <para>
/// Every one of them but the four that start or list a session takes a session id, and reaches it
/// through <see cref="LiveAppSessionManager"/>, which serves only the calling client its own.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LiveAppDebugTools(LiveAppSessionManager sessions)
{
	[McpServerTool(
		Name = ToolNames.DebugAttach,
		Title = "Attach a debugger to a process",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Attach a debugger to a running .NET process by its id and start a session over it, without "
			+ "Visual Studio and without pausing it. The target keeps running; exceptions, log messages "
			+ "and module loads are captured for rose_debug_events to read. Local, same-user processes "
			+ "only. Returns the session id to pass to rose_debug_events and rose_debug_detach.")]
	public async Task<LiveAppSessionSummary> AttachAsync(
		[Description(ToolDescriptions.ProcessIdArgument)]
		int processId,
		CancellationToken cancellationToken = default)
	{
		LocalAttachPolicy.EnsureAttachable(processId);

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.AttachProcess,
			ProcessId = processId,
			Description = DescribeProcess(processId),
		};

		var session = await sessions.StartAsync(target, cancellationToken);
		var summary = session.Describe();

		if (summary.State == LiveAppSessionState.Faulted)
		{
			// The host is alive but could not attach; reclaim it and tell the caller why.
			await sessions.CloseAsync(session.SessionId, cancellationToken);
			throw new McpException(summary.Detail ?? $"Could not attach to pid {processId}.");
		}

		return summary;
	}

	[McpServerTool(
		Name = ToolNames.DebugLaunch,
		Title = "Launch a process under the debugger",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Launch a local .NET executable under the debugger and start a session over it from startup, so "
			+ "its earliest module loads and exceptions are captured -- which attaching after the fact "
			+ "cannot see. The program runs as the current user, the same as launching it yourself. Set "
			+ "breakpoints once it is running the way you would after an attach. Returns the session id for "
			+ "rose_debug_events and rose_debug_detach. Detaching leaves it running.")]
	public async Task<LiveAppSessionSummary> LaunchAsync(
		[Description(ToolDescriptions.ExecutablePathArgument)] string executablePath,
		[Description(ToolDescriptions.LaunchArgumentsArgument)] string? arguments = null,
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(executablePath)) throw new McpException($"No executable at {executablePath}.");

		var fullPath = Path.GetFullPath(executablePath);
		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = fullPath,
			Arguments = arguments,
			Description = $"{Path.GetFileName(fullPath)} (launched)",
		};

		var session = await sessions.StartAsync(target, cancellationToken);
		var summary = session.Describe();

		if (summary.State == LiveAppSessionState.Faulted)
		{
			await sessions.CloseAsync(session.SessionId, cancellationToken);
			throw new McpException(summary.Detail ?? $"Could not launch {fullPath}.");
		}

		return summary;
	}

	[McpServerTool(
		Name = ToolNames.DebugLaunchUwp,
		Title = "Launch a UWP app under the debugger",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Activate a packaged (UWP) app under the debugger by its app user-model id (PackageFamilyName!App) "
			+ "and start a session over it. The package is put in debug mode (no suspension, no activation "
			+ "timeout) and activated, then attached -- so a classic UWP app, which has no ARM64 runtime and "
			+ "runs x64 emulated, is debugged through the x64 host automatically. The app must already be "
			+ "deployed. Detaching leaves it running and lifts debug mode. Returns the session id.")]
	public async Task<LiveAppSessionSummary> LaunchUwpAsync(
		[Description(ToolDescriptions.AppUserModelIdArgument)] string appUserModelId,
		CancellationToken cancellationToken = default)
	{
		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = appUserModelId,
			Description = $"{appUserModelId} (UWP)",
		};

		var session = await sessions.StartAsync(target, cancellationToken);
		var summary = session.Describe();

		if (summary.State == LiveAppSessionState.Faulted)
		{
			await sessions.CloseAsync(session.SessionId, cancellationToken);
			throw new McpException(summary.Detail ?? $"Could not launch {appUserModelId}.");
		}

		return summary;
	}

	[McpServerTool(
		Name = ToolNames.DebugEvents,
		Title = "Read debug events",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Debug events captured since a cursor: first-chance and unhandled exceptions, Debugger.Log "
			+ "messages, module loads, breakpoint hits and step completions. Pass the returned "
			+ "nextCursor as 'after' next time to get only what is new; a cursor below "
			+ "oldestAvailable means the buffer dropped events between them. Filter with 'kinds' -- a "
			+ "freshly started app produces hundreds of ModuleLoaded events, and asking for LogMessage "
			+ "or ExceptionFirstChance alone is the difference between a readable answer and one that "
			+ "has to be written to a file. Use waitSeconds with kinds to wait for one thing, such as "
			+ "BreakpointHit, rather than calling this in a loop.")]
	public async Task<LiveDebugEventPage> EventsAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.AfterSequenceArgument)]
		long after = 0,
		[Description(ToolDescriptions.EventKindsArgument)]
		string[]? kinds = null,
		[Description(ToolDescriptions.MaxEventsArgument)]
		int limit = 500,
		[Description(ToolDescriptions.WaitSecondsArgument)]
		int waitSeconds = 0,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadEventsAsync(after, kinds, limit, waitSeconds, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugDetach,
		Title = "Detach and end a session",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false)]
	[Description(
		"Detach the debugger and end the session, leaving the target process running exactly as before. "
			+ "Use this to stop watching a process without killing it; a debugger that is simply abandoned "
			+ "would otherwise risk taking the target down with it, which detaching avoids. It fails rather "
			+ "than reporting success if the debugger could not be detached, since that is the one outcome "
			+ "where the target is at risk.")]
	public async Task<string> DetachAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		// Held before the close, because closing forgets it, and its answer to "did the detach
		// actually happen" is the only thing that makes the sentence below true rather than habitual.
		var session = sessions.Find(sessionId);

		var closed = await sessions.CloseAsync(sessionId, cancellationToken);
		if (!closed) return "That session was not open.";

		if (session?.DetachFailure is { Length: > 0 } failure)
		{
			throw new McpException(
				$"The session is closed, but the debugger could not be detached from the target: {failure} "
					+ "The debugging interface was deliberately left open rather than terminated, because "
					+ "terminating it while attached kills the target -- so the target should still be running, "
					+ "but it is no longer being watched and nothing here can confirm the debugger is off it.");
		}

		return "Detached; the target keeps running.";
	}

	[McpServerTool(
		Name = ToolNames.DebugList,
		Title = "List debug sessions",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"List the live-app debug sessions the broker is supervising, each with its session id, target, "
			+ "architecture, state, and process ids. Use it to recover a session id you did not keep from "
			+ "rose_debug_attach, or to see what is currently attached before starting another session.")]
	public LiveAppSessionList List() => new() { Sessions = sessions.DescribeOwned() };

	[McpServerTool(
		Name = ToolNames.DebugAddTracepoint,
		Title = "Add a tracepoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Add a tracepoint at a method by name: a breakpoint that logs and immediately continues, so it "
			+ "never freezes the target the way a stopping breakpoint would -- the right default for a "
			+ "turn-based agent. Each hit appears in rose_debug_events. Prefer this over adding logging "
			+ "statements and rebuilding, which needs a source edit and a restart to see anything. It binds "
			+ "when the method's module is loaded, so an as-yet-unloaded module reads back as not bound.")]
	public async Task<LiveTracepoint> AddTracepointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.TracepointLocationArgument)]
		string location,
		[Description(ToolDescriptions.LogMessageArgument)]
		string? logMessage = null,
		[Description(ToolDescriptions.LogEveryNthHitArgument)]
		int? logEveryNthHit = null,
		[Description(ToolDescriptions.TracepointConditionArgument)]
		string? condition = null,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.AddTracepointAsync(location, logMessage, logEveryNthHit, condition, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugListTracepoints,
		Title = "List tracepoints",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"List a session's tracepoints, each with its id, location, hit count, and whether it is bound "
			+ "yet. Use it to confirm a tracepoint bound to a real method, since one whose module has not "
			+ "loaded, or whose method name did not resolve, stays unbound and reports why rather than "
			+ "failing loudly.")]
	public async Task<LiveTracepointList> ListTracepointsAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ListTracepointsAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugRemoveTracepoint,
		Title = "Remove a tracepoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Remove a tracepoint by id and return the remaining set. Use it to stop a tracepoint once you "
			+ "have seen what you needed, rather than leaving a hot-path log running for the life of the "
			+ "session; removing an id that is already gone is harmless and simply returns the current set.")]
	public async Task<LiveTracepointList> RemoveTracepointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.TracepointIdArgument)] string tracepointId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.RemoveTracepointAsync(tracepointId, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugSetBreakpoint,
		Title = "Set a stopping breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Set a stopping breakpoint at a method by name: on hit it pauses the target and records the "
			+ "stop with its call stack in rose_debug_events, so you can see how execution got there. The "
			+ "target stays frozen until rose_debug_continue, or until an auto-continue safety timeout "
			+ "(default 30s) fires so an unattended stop cannot wedge the app -- so read the events and "
			+ "continue promptly. For non-invasive logging that never pauses, prefer rose_debug_add_tracepoint.")]
	public async Task<LiveBreakpoint> SetBreakpointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.BreakpointLocationArgument)]
		string location,
		[Description(ToolDescriptions.AutoContinueSecondsArgument)]
		int? autoContinueSeconds = null,
		[Description(ToolDescriptions.BreakpointConditionArgument)]
		string? condition = null,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.SetBreakpointAsync(location, autoContinueSeconds, condition, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugListBreakpoints,
		Title = "List stopping breakpoints",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"List a session's stopping breakpoints, each with its id, location, hit count, auto-continue "
			+ "timeout, and whether it is bound yet. Use it to confirm a breakpoint bound to a real method, "
			+ "since one whose module has not loaded, or whose method name did not resolve, stays unbound "
			+ "and reports why rather than failing loudly.")]
	public async Task<LiveBreakpointList> ListBreakpointsAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ListBreakpointsAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugRemoveBreakpoint,
		Title = "Remove a stopping breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Remove a stopping breakpoint by id and return the remaining set. Use it once you have seen what "
			+ "you needed so execution stops passing through that method; removing one the target is "
			+ "currently held at does not itself resume -- call rose_debug_continue for that.")]
	public async Task<LiveBreakpointList> RemoveBreakpointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.BreakpointIdArgument)] string breakpointId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.RemoveBreakpointAsync(breakpointId, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugContinue,
		Title = "Continue from a breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false)]
	[Description(
		"Resume a target that is held at a stopping breakpoint, so it keeps running. Call this after you "
			+ "have read the stop and its stack from rose_debug_events; it is a no-op if nothing is "
			+ "currently stopped, which is safe to call speculatively.")]
	public async Task<string> ContinueAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		var continued = await session.ContinueAsync(cancellationToken);
		return continued ? "Continued; the target is running again." : "Nothing was stopped at a breakpoint.";
	}

	[McpServerTool(
		Name = ToolNames.DebugStep,
		Title = "Step",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false)]
	[Description(
		"Step a target that is held at a breakpoint: 'in' steps into calls, 'over' runs them without "
			+ "descending, 'out' runs to the caller. The step resumes the target briefly and then holds it "
			+ "again at the new location, which arrives as a StepComplete event in rose_debug_events with a "
			+ "fresh stack and locals. It is a no-op if nothing is currently stopped. Line granularity "
			+ "needs a PDB; without one, a step lands at the runtime's own step boundaries.")]
	public async Task<string> StepAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.StepModeArgument)] string mode = "over",
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		var stepped = await session.StepAsync(mode, cancellationToken);
		return stepped ? $"Stepped {mode}; see the StepComplete event for the new location." : "Nothing was stopped to step.";
	}

	[McpServerTool(
		Name = ToolNames.DebugEvaluate,
		Title = "Evaluate an expression at a stop",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Evaluates a field-access chain against a target held at a breakpoint or a step: an argument "
			+ "or local name, then .field into the object graph. It reads fields from memory and runs "
			+ "none of the debuggee's own code, so it never hangs or changes the target -- property "
			+ "getters and method calls are deliberately not evaluated. Only valid while stopped. "
			+ "Arguments and locals go by the names the breakpoint's recorded frame reports: the names "
			+ "the source declares where the module has symbols beside it, and local_0, local_1 in slot "
			+ "order where it has none. Returns the value and its type, or why it did not resolve.")]
	public async Task<LiveEvaluation> EvaluateAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.EvaluateExpressionArgument)] string expression,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.EvaluateAsync(expression, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlTree,
		Title = "Read the live XAML visual tree",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Reads a snapshot of a running app's live XAML visual tree: a flat list of elements, each with "
			+ "a stable handle, its parent handle and child index to rebuild the tree from, its type, "
			+ "its x:Name where it has one, and an address that names it even where it has not. It "
			+ "injects a diagnostics provider into the target and enumerates on the app's UI thread. "
			+ "The target must be a XAML app (UWP or WinUI); one with no XAML UI, or a build with no "
			+ "provider, comes back with a detail and no nodes rather than failing.")]
	public async Task<LiveXamlTree> XamlTreeAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlRootArgument)] string? root = null,
		[Description(ToolDescriptions.XamlOffsetArgument)] int offset = 0,
		[Description(ToolDescriptions.XamlLimitArgument)] int limit = 0,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlTreeAsync(root, offset, limit, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlProperties,
		Title = "Read a XAML element's properties",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Reads one element's XAML properties. Each comes with its value, type and provenance -- Local, "
			+ "Style, Inherited, Animation, Default -- so what the markup sets can be told from a "
			+ "framework default, and with the file and line that set it where the app carries source "
			+ "info. This is the bridge from a live element to its XAML source. Set properties only by "
			+ "default; includeDefaults gives the full set. One caveat, and it is measured: reading an "
			+ "element brings its untouched collection properties into existence, so a second read "
			+ "reports a few more as Local than the first -- on a TextBlock, Inlines, TextHighlighters "
			+ "and SelectionHighlightColor. The first read is the accurate one; do not treat properties "
			+ "that appear between two reads as something an edit did.")]
	public async Task<LiveXamlProperties> XamlPropertiesAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlElementArgument)] string element,
		[Description(ToolDescriptions.IncludeDefaultsArgument)]
		bool includeDefaults = false,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlPropertiesAsync(
			await session.ResolveElementAsync(element, cancellationToken), includeDefaults, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlApply,
		Title = "Live-edit XAML",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Applies a XAML file's current text to the running app's visual tree, no relaunch -- what "
			+ "Visual Studio calls XAML Hot Reload. Pass filePath; the session diffs it against what it "
			+ "last sent, so the loop is edit, apply, edit, apply. Reach for it instead of rebuilding and "
			+ "relaunching to see a layout or a colour change. The first call for a file records a "
			+ "baseline and applies nothing: make it before editing, or pass oldXaml with filePath. "
			+ "Property changes, added and removed elements and changed resources apply, to named "
			+ "and unnamed elements alike -- an unnamed one by the address rose_xaml_tree and "
			+ "rose_xaml_selection report. For markup not on disk, pass oldXaml and newXaml. Read the "
			+ "notes: they list edits worked out but not applied, and a failed edit is not retried by "
			+ "the next apply, since re-sending an added element would add a second copy. The change "
			+ "lives on the objects in the tree rather than in the app's markup, so it is gone if the "
			+ "app rebuilds that part of the UI.")]
	public async Task<LiveXamlApplyResult> XamlApplyAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlFilePathArgument)]
		string? filePath = null,
		[Description(ToolDescriptions.XamlOldMarkupArgument)]
		string? oldXaml = null,
		[Description(ToolDescriptions.XamlNewMarkupArgument)]
		string? newXaml = null,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ApplyXamlAsync(oldXaml, newXaml, filePath, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlSelection,
		Title = "Read the selected XAML element",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Reads the element the user picked by clicking it in the running app, with the whole stack "
			+ "under that click, topmost first -- a click usually lands on an unnamed part of a "
			+ "template, so the container above it is often what was meant. Each carries a handle and an "
			+ "address, either of which rose_xaml_properties and rose_xaml_apply take. Read it before "
			+ "asking the user to point at anything: they can arm select mode themselves from the "
			+ "in-app toolbar, so an answer may already be waiting. Nothing picked reads as empty "
			+ "rather than failing.")]
	public async Task<LiveXamlSelection> XamlSelectionAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlSelectionAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlDeselect,
		Title = "Clear the selected XAML element",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Clears the picked element and the mark drawn over the running app. "
			+ "Call it when you have finished with a selection: the mark stays on screen until "
			+ "something clears it, and a person left looking at it has no way to know the tool is done "
			+ "with it.")]
	public async Task<LiveXamlSelection> XamlDeselectAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ClearXamlSelectionAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlSelectElement,
		Title = "Select a XAML element by handle",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Selects one element without a click, reaching what a click cannot: something behind another "
			+ "element, or a part of a template a person cannot hit. Takes a handle, an x:Name as "
			+ "#name, or the address rose_xaml_tree reports, and answers with the same shape "
			+ "rose_xaml_selection does, so the two are interchangeable from there on. It marks the "
			+ "element in the app, so the user can see what was picked. A name matching several "
			+ "elements is refused rather than one of them chosen.")]
	public async Task<LiveXamlSelection> XamlSelectElementAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlElementArgument)] string element,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);

		return await session.SelectXamlElementAsync(
			await session.ResolveElementAsync(element, cancellationToken), cancellationToken);
	}

	private LiveAppSession Require(string sessionId)
		=> sessions.Find(sessionId)
			?? throw new McpException(
				$"No debug session '{sessionId}' is open for this client. rose_debug_list names the ones there "
					+ "are. A session another client of this broker started belongs to it and is not reachable "
					+ "from here.");

	private static string DescribeProcess(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return $"{process.ProcessName} (pid {processId})";
		}
		catch (Exception)
		{
			return $"pid {processId}";
		}
	}
}
