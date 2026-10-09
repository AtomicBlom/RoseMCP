using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Workspace lifecycle and health, scoped to the single solution this worker owns.</summary>
[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceCalls calls)
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
		[Description(ToolDescriptions.IncludeProjectsArgument)] bool includeProjects = false,
		CancellationToken cancellationToken = default) =>
		StatusProjects.Listed(await calls.StatusAsync(progress, cancellationToken), includeProjects);
}
