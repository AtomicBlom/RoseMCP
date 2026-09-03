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
			+ "them.")]
	public async Task<LiveDebugEventPage> EventsAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("Return only events whose sequence is greater than this; 0 for everything buffered.")]
		long after = 0,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadEventsAsync(after, cancellationToken);
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
			+ "would otherwise risk taking the target down with it, which detaching avoids.")]
	public async Task<string> DetachAsync(
		[Description(SessionHelp)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var closed = await sessions.CloseAsync(sessionId, cancellationToken);
		return closed ? "Detached; the target keeps running." : "That session was not open.";
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
	public IReadOnlyList<LiveAppSessionSummary> List() => sessions.Describe();

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
	public async Task<IReadOnlyList<LiveTracepoint>> ListTracepointsAsync(
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
	public async Task<IReadOnlyList<LiveTracepoint>> RemoveTracepointAsync(
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
	public async Task<IReadOnlyList<LiveBreakpoint>> ListBreakpointsAsync(
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
	public async Task<IReadOnlyList<LiveBreakpoint>> RemoveBreakpointAsync(
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
			+ "minimal set of edits and each is applied and reported back with its outcome. Property changes "
			+ "on named (x:Name) elements apply today -- a colour, a size, a piece of text; structural changes "
			+ "and unnamed elements are reported as not-yet-applied rather than dropped. Use it after editing a "
			+ "XAML file: pass what was on disk before and what is there now.")]
	public async Task<LiveXamlReloadResult> XamlApplyAsync(
		[Description(SessionHelp)] string sessionId,
		[Description("The previous XAML (what the file held before the edit).")] string oldXaml,
		[Description("The new XAML to apply (what the file holds now).")] string newXaml,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReloadXamlAsync(oldXaml, newXaml, cancellationToken);
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
