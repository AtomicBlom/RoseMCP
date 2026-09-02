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
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.AddTracepointAsync(location, logMessage, logEveryNthHit, cancellationToken);
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
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.SetBreakpointAsync(location, autoContinueSeconds, cancellationToken);
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
