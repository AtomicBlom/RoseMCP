using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>Facts about this live-app host and its target, for the broker's own bookkeeping.</summary>
[McpServerToolType]
public sealed class LiveAppInfoTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppInfo,
		Title = "Live-app host information",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Host process id, the architecture it launched as, and whether it established its target.")]
	public LiveAppInfo Info() => host.CurrentInfo();
}
