using System.ComponentModel;
using System.Diagnostics;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// The agent-facing debugging surface. Each tool drives a per-target live-app session the broker
/// supervises, the debugging counterpart to the per-solution workspace tools. This is the first
/// dogfoodable slice: attach to a running .NET process, watch its exceptions and log output, detach.
/// </summary>
[McpServerToolType]
public sealed class LiveAppDebugTools(LiveAppSessionManager sessions)
{
	private const string SessionHelp = "The session id returned by rose_debug_attach.";

	[McpServerTool(
		Name = ToolNames.DebugAttach,
		Title = "Attach a debugger to a process",
		ReadOnly = true,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Attach a debugger to a running .NET process by its id and start a session over it, without "
			+ "Visual Studio and without pausing it. The target keeps running; exceptions, log messages "
			+ "and module loads are captured for rose_debug_events to read. Local, same-user processes "
			+ "only. Returns the session id to pass to rose_debug_events and rose_debug_detach.")]
	public async Task<LiveAppSessionSummary> AttachAsync(
		[Description("The process id to attach to. Must be a local process owned by the current user.")]
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
		[Description("Path to a local .NET executable (.exe).")] string executablePath,
		[Description("Optional command-line arguments.")] string? arguments = null,
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
		[Description("The app user-model id, e.g. MyApp_1a2b3c4d5e6f7!App.")] string appUserModelId,
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
			+ "messages, and module loads. Pass the returned nextCursor as 'after' next time to get only "
			+ "what is new. If your cursor is below oldestAvailable, the buffer dropped events between "
			+ "them. Filter with 'kinds' -- a freshly started app produces hundreds of ModuleLoaded "
			+ "events, and asking for LogMessage or ExceptionFirstChance alone is the difference between "
			+ "a readable answer and one that has to be written to a file: "
			+ "SessionNotice, ProcessCreated, ProcessExited, ModuleLoaded, ThreadCreated, ThreadExited, "
			+ "ExceptionFirstChance, ExceptionUnhandled, LogMessage, BreakpointHit, StepComplete.")]
	public async Task<LiveDebugEventPage> EventsAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("Return only events whose sequence is greater than this; 0 for everything buffered.")]
		long after = 0,
		[Description("Comma-separated event kinds to return; omit for all. The cursor still advances over what is filtered out, and 'skipped' says how many those were.")]
		string? kinds = null,
		[Description("Maximum events in this page (default 500). Lower it when you only need to see whether something is happening.")]
		int limit = 500,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadEventsAsync(after, kinds, limit, cancellationToken);
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
		[Description(SessionHelp)] string sessionId,
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
	public LiveAppSessionList List() => new() { Sessions = sessions.Describe() };

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
		[Description(SessionHelp)] string sessionId,
		[Description("Method to trace, as [Assembly!]Namespace.Type.Method, e.g. MyApp.Widget.Refresh.")]
		string location,
		[Description("Optional message logged on each hit (literal text; expression interpolation comes later).")]
		string? logMessage = null,
		[Description("Optional: log only every Nth hit to thin a hot path; every hit is still counted.")]
		int? logEveryNthHit = null,
		[Description("Optional condition gating each hit, as 'name OP literal' over the method's arguments/locals, e.g. count >= 100. Only simple value compares; expressions need eval.")]
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
		[Description(SessionHelp)] string sessionId,
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
		[Description(SessionHelp)] string sessionId,
		[Description("The tracepoint id returned by rose_debug_add_tracepoint.")] string tracepointId,
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
		[Description(SessionHelp)] string sessionId,
		[Description("Method to break on, as [Assembly!]Namespace.Type.Method, e.g. MyApp.Widget.Refresh.")]
		string location,
		[Description("Seconds a hit is held before the target auto-continues on its own; default 30.")]
		int? autoContinueSeconds = null,
		[Description("Optional condition gating each hit, as 'name OP literal' over the method's arguments/locals, e.g. id == 42. Only simple value compares; expressions need eval.")]
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
		[Description(SessionHelp)] string sessionId,
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
		[Description(SessionHelp)] string sessionId,
		[Description("The breakpoint id returned by rose_debug_set_breakpoint.")] string breakpointId,
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
		[Description(SessionHelp)] string sessionId,
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
		[Description(SessionHelp)] string sessionId,
		[Description("in, over, or out.")] string mode = "over",
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
		"Evaluate a simple expression against a target held at a breakpoint or step: a field-access chain "
			+ "-- an argument or local name, then .field into the object graph (e.g. state.Inner.Count). It "
			+ "reads fields directly from memory and runs none of the debuggee's own code, so it never hangs "
			+ "or changes the target; property getters and method calls are deliberately not evaluated. Only "
			+ "valid while stopped. Local names need a PDB; arguments are always named. Returns the value and "
			+ "its type, or an error explaining why it did not resolve.")]
	public async Task<LiveEvaluation> EvaluateAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("A field-access expression, e.g. this.field or state.Inner.Count.")] string expression,
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
		"Read a snapshot of a running app's live XAML visual tree. It injects a diagnostics provider into "
			+ "the target and enumerates the tree on the app's UI thread, returning a flat list of elements "
			+ "-- each with a stable handle, its parent handle and child index (rebuild the tree from those), "
			+ "its type, and its x:Name when it has one. The target must be a XAML app (UWP/WinUI); for one "
			+ "with no XAML UI, or when the provider is not built, the result carries a detail and no nodes "
			+ "rather than failing. Use it to see the live tree of an app started with rose_debug_launch_uwp.")]
	public async Task<LiveXamlTree> XamlTreeAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("Root the tree at this named element's subtree; omit for the whole tree.")] string? rootName = null,
		[Description("Skip this many nodes, for paging a large tree.")] int offset = 0,
		[Description("Return at most this many nodes; 0 for all. Total says how many matched.")] int limit = 0,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlTreeAsync(rootName, offset, limit, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlProperties,
		Title = "Read a XAML element's properties",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Read one element's XAML properties, given the handle from rose_xaml_tree. Each property comes "
			+ "with its value, type, and provenance -- Local (set on the element), Style, Inherited, "
			+ "Animation, Default, and so on -- so you can tell what the XAML actually sets from framework "
			+ "defaults; when the app carries source info, each also carries the file and line that set it. "
			+ "Set (non-default) properties only by default; pass includeDefaults for the full set. This is "
			+ "the bridge from a live element to its XAML source.")]
	public async Task<LiveXamlProperties> XamlPropertiesAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("The element handle from rose_xaml_tree.")] ulong handle,
		[Description("Include framework default values, not only the ones the XAML sets.")] bool includeDefaults = false,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlPropertiesAsync(handle, includeDefaults, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlApply,
		Title = "Hot-reload XAML",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Hot-reload a running XAML app: diff two versions of its XAML and apply the changes to the live "
			+ "visual tree, no relaunch. Pass the previous XAML and the new XAML; the diff reduces to the "
			+ "minimal set of edits and each is applied and reported back with its outcome. Property "
			+ "changes, added elements, removed elements and changed resources all apply -- a colour, a "
			+ "size, a piece of text, a whole new element with its own children, a brush in a resource "
			+ "dictionary -- on any element the diff can address, named or not. An element with no x:Name "
			+ "is addressed by the path rose_xaml_tree and rose_xaml_selection report as its address, so a "
			+ "click inside a control template is targetable. Use it after editing a XAML file: pass what "
			+ "was on disk before and what is there now. Read the notes as well as the results: they name "
			+ "the edits it worked out but does not apply, such as adding or removing a resource, and the "
			+ "fact that an element added live cannot carry an x:Name.")]
	public async Task<LiveXamlReloadResult> XamlApplyAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("The previous XAML (what the file held before the edit).")] string oldXaml,
		[Description("The new XAML to apply (what the file holds now).")] string newXaml,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlSelectMode,
		Title = "Enter XAML select mode",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Arm select mode on a running XAML app: the next click in the app picks that element instead of "
			+ "reaching the app. Use it to get a visual path to an element -- ask the user to click the one "
			+ "they mean, then call rose_xaml_selection to find out which it was. The user can also arm it "
			+ "themselves from RoseMCP's in-app toolbar, so a user who says \"look at the element I selected\" "
			+ "may already have picked one: call rose_xaml_selection first and only arm if nothing is there. "
			+ "The selection carries the whole stack under the click, topmost first, so you can walk down "
			+ "to a templated child or up to the container without arming again; each handle feeds "
			+ "rose_xaml_properties and rose_xaml_apply directly.")]
	public async Task<LiveXamlSelection> XamlSelectModeAsync(
		[Description(SessionHelp)] string sessionId,
		[Description(
			"Also pick elements the framework would not route a click to -- an empty Grid with no "
				+ "Background, something with IsHitTestVisible false. Off by default, because such an "
				+ "element can cover the whole window and shadow everything the user can actually click. "
				+ "Turn it on only to inspect an invisible host deliberately.")]
		bool includeAllElements = false,
		[Description(
			"Prefer the element the app's own XAML declares over a control template's parts, the way "
				+ "Visual Studio's Just My XAML does. On by default: a click on a button means the button "
				+ "the developer wrote, not whichever templated child is topmost. Decided on the element's "
				+ "source -- ms-appx: is the app's markup, ms-resource: is the framework's -- and it falls "
				+ "back to the framework's own pick when nothing under the click came from the app. Turn it "
				+ "off to select template internals.")]
		bool justMyXaml = true,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.EnterXamlSelectModeAsync(includeAllElements, justMyXaml, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlSelection,
		Title = "Read the selected XAML element",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Read the element the user picked by clicking it in the app -- whether they armed select mode from "
			+ "RoseMCP's in-app toolbar or you armed it with rose_xaml_select_mode. Returns its type, its "
			+ "x:Name when it has one, the stable handle, and its address. Pass the handle to "
			+ "rose_xaml_properties to see what the XAML sets on it, and use the address as the element's "
			+ "identity in rose_xaml_apply's diff to change it -- the address works whether or not the markup "
			+ "named it, which matters because a click usually lands on an unnamed part of a template. Every "
			+ "candidate in the stack carries one too, so choosing an ancestor still leaves something "
			+ "targetable. If nobody has picked yet it says so, and whether select mode is armed, so it is "
			+ "safe to poll while waiting for the user.")]
	public async Task<LiveXamlSelection> XamlSelectionAsync(
		[Description(SessionHelp)] string sessionId,
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
		"Clear the picked element: both the recorded selection and the mark RoseMCP draws over it in the "
			+ "running app. Use it when you are done with an element, since the mark is deliberately "
			+ "persistent -- it stays until something replaces it, which is what makes \"the selected "
			+ "element\" mean something to you and the user at once, and also means the user is left "
			+ "looking at it. The two halves always go together: clearing one and not the other would "
			+ "either leave a mark over nothing or report a selection nobody can see. It reports whether "
			+ "there was anything to clear, so it is safe to call when you are not sure.")]
	public async Task<LiveXamlSelection> XamlDeselectAsync(
		[Description(SessionHelp)] string sessionId,
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
		"Select an element by the handle rose_xaml_tree gave it, with no click involved. Use this to "
			+ "pick an element structurally -- by type, by x:Name, by the source file its markup came "
			+ "from -- instead of asking the user to click it, and use it when a click cannot reach the "
			+ "element at all: a slider is the known case, because what a click resolves to is the "
			+ "framework's answer and it is sometimes not the element anybody meant. It marks the "
			+ "element in the running app exactly as a click would, so the user can see what you picked, "
			+ "and it returns the same stack a click does -- the element first, then its ancestors "
			+ "outwards, so you can go up to the container you actually meant without asking again. "
			+ "Each handle feeds rose_xaml_properties and rose_xaml_apply directly.")]
	public async Task<LiveXamlSelection> XamlSelectElementAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("The element's handle, from rose_xaml_tree.")] ulong handle,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.SelectXamlElementAsync(handle, cancellationToken);
	}

	private LiveAppSession Require(string sessionId)
		=> sessions.Find(sessionId) ?? throw new McpException($"No debug session '{sessionId}' is open.");

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
