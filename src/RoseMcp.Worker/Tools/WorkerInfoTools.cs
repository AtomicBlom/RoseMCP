using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;
using RoseMcp.Logging;

namespace RoseMcp.Worker.Tools;

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
	[Description("Process id, managed heap size and log file for this worker. Does not load anything.")]
	public WorkerInfo Info() => new()
	{
		ProcessId = Environment.ProcessId,
		SolutionPath = options.SolutionPath,
		ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
		LogPath = RoseFileLogging.Destination,
	};
}
