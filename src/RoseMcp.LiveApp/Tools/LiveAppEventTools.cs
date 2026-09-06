using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>The buffered debug events this host has captured, read by the broker for the agent.</summary>
[McpServerToolType]
public sealed class LiveAppEventTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppEvents,
		Title = "Live-app debug events",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Buffered debug events with a sequence above the given cursor, and the session's state.")]
	public Task<LiveDebugEventPage> Events(
		[Description(ToolDescriptions.AfterSequenceArgument)]
		long after = 0,
		[Description(ToolDescriptions.EventKindsArgument)]
		string? kinds = null,
		[Description(ToolDescriptions.MaxEventsArgument)]
		int limit = 500,
		[Description(ToolDescriptions.WaitSecondsArgument)]
		int waitSeconds = 0,
		CancellationToken cancellationToken = default)
		=> host.ReadEventsAsync(after, ArgumentValues.EventKinds(kinds), limit, waitSeconds, cancellationToken);
}
