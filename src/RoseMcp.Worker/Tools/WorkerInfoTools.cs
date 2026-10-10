using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;
using RoseMcp.Logging;

namespace RoseMcp.Worker.Tools;

/// <summary>Facts about this worker process, for the broker's own bookkeeping.</summary>
[McpServerToolType]
public sealed class WorkerInfoTools(WorkerOptions options, WorkspaceCalls calls)
{
	/// <summary>
	/// Who this worker is, and what its reads have found. The rebuilt analyzers are what the reads found rather
	/// than a check of its own, so this stays a call that touches nothing and never waits behind the writer --
	/// and the broker, which asks after every call, hears of one from the call that found it.
	/// </summary>
	[McpServerTool(
		Name = ToolNames.WorkerInfo,
		Title = "Worker process information",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Process id, managed heap size and log file for this worker. Does not load anything.")]
	public WorkerInfo Info()
	{
		var rebuilt = calls.RebuiltAnalyzerPaths;

		return new()
		{
			ProcessId = Environment.ProcessId,
			SolutionPath = options.SolutionPath,
			ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
			LogPath = RoseFileLogging.Destination,
			RebuiltAnalyzers = rebuilt,
			RebuiltAnalyzersNotice = RebuiltAnalyzers.Notice(rebuilt),
		};
	}
}
