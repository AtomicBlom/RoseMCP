using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Workspace lifecycle and health, scoped to the single solution this worker owns.</summary>
[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceHost host, SharedWorkProgress sharedWork)
{
	[McpServerTool(
		Name = ToolNames.WorkspaceStatus,
		Title = "Roslyn workspace status",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.WorkspaceStatus)]
	public async Task<WorkspaceStatusReport> StatusAsync(
		IProgress<ProgressNotificationValue> progress,
		CancellationToken cancellationToken)
	{
		// The first status call after a worker starts is usually the one waiting for the whole
		// design-time build, so the load gets the first half of the bar and describing the result --
		// which is where generators actually run -- gets the second.
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		return await host.GetStatusAsync(cancellationToken, working);
	}
}
