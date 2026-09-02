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
