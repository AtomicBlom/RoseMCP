using System.ComponentModel;

using ModelContextProtocol.Server;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker.Tools;

/// <summary>Workspace lifecycle and health, scoped to the single solution this worker owns.</summary>
[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceHost host)
{
	[McpServerTool(
		Name = ToolNames.WorkspaceStatus,
		Title = "Roslyn workspace status",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Reports what this worker has loaded and whether its answers can be trusted: per-project load
        state, document and source-generated document counts, what restore did, and any reason the
        workspace is degraded. Check this first when diagnostics or generated code look wrong -- a
        degraded workspace returns plausible but incomplete results rather than errors.
        """)]
	public Task<WorkspaceStatusReport> StatusAsync(CancellationToken cancellationToken) =>
		host.GetStatusAsync(cancellationToken);
}
