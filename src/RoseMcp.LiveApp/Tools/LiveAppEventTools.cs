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
	public LiveDebugEventPage Events(
		[Description("Return only events whose sequence is greater than this; 0 for everything buffered.")]
		long after = 0)
		=> host.ReadEvents(after);
}
