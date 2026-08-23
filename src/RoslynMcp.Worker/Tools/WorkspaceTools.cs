using System.ComponentModel;

using ModelContextProtocol.Server;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker.Tools;

/// <summary>Workspace lifecycle and health, scoped to the single solution this worker owns.</summary>
[McpServerToolType]
public sealed class WorkspaceTools(WorkerOptions options)
{
	[McpServerTool(Name = ToolNames.WorkspaceStatus, ReadOnly = true, Title = "Roslyn workspace status")]
	[Description("Reports what this worker has loaded: per-project load state, document and generated-document counts, degraded-load causes, and the current revision.")]
	public string Status() => options.SolutionPath;
}
