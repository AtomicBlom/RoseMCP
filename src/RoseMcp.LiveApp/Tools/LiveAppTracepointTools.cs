using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>Tracepoint management for this host's target, forwarded by the broker.</summary>
[McpServerToolType]
public sealed class LiveAppTracepointTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppAddTracepoint,
		Title = "Add tracepoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Add tracepoints at methods by name; each logs and auto-continues without pausing, and each request's outcome is its own entry.")]
	public LiveTracepointBatch Add(
		[Description(ToolDescriptions.TracepointsArgument)] AddTracepointRequest[] tracepoints)
		=> host.AddTracepoints(tracepoints);

	[McpServerTool(
		Name = ToolNames.LiveAppListTracepoints,
		Title = "List tracepoints",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("This session's tracepoints and whether each is bound.")]
	public LiveTracepointList List() => host.ListTracepoints();

	[McpServerTool(
		Name = ToolNames.LiveAppRemoveTracepoint,
		Title = "Remove tracepoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Remove tracepoints by id, each id's outcome its own entry, returning the remaining set.")]
	public LiveTracepointRemoval Remove(
		[Description(ToolDescriptions.TracepointIdsArgument)] string[] tracepointIds)
		=> host.RemoveTracepoints(tracepointIds);
}
