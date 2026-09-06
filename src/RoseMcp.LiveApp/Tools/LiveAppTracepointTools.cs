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
		Title = "Add a tracepoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Add a tracepoint at a method by name; it logs and auto-continues without pausing.")]
	public LiveTracepoint Add(
		[Description(ToolDescriptions.TracepointLocationArgument)] string location,
		[Description(ToolDescriptions.LogMessageArgument)] string? logMessage = null,
		[Description(ToolDescriptions.LogEveryNthHitArgument)] int? logEveryNthHit = null,
		[Description(ToolDescriptions.TracepointConditionArgument)] string? condition = null)
		=> host.AddTracepoint(location, logMessage, logEveryNthHit, condition);

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
		Title = "Remove a tracepoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Remove a tracepoint by id, returning the remaining set.")]
	public LiveTracepointList Remove(
		[Description(ToolDescriptions.TracepointIdArgument)] string tracepointId)
		=> host.RemoveTracepoint(tracepointId);
}
