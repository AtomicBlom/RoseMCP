using System.ComponentModel;

using ModelContextProtocol.Server;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker.Tools;

/// <summary>Facts about this worker process, for the broker's own bookkeeping.</summary>
[McpServerToolType]
public sealed class WorkerInfoTools(WorkerOptions options)
{
	[McpServerTool(
		Name = ToolNames.WorkerInfo,
		Title = "Worker process information",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Process id and managed heap size for this worker. Does not load anything.")]
	public WorkerInfo Info() => new()
	{
		ProcessId = Environment.ProcessId,
		SolutionPath = options.SolutionPath,
		ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
	};
}
