using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>The live XAML surface this host serves by injecting the diagnostics provider into its target.</summary>
[McpServerToolType]
public sealed class LiveAppXamlTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppXamlTree,
		Title = "Live-app XAML visual tree",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Inject the XAML diagnostics provider into the target and read a snapshot of its live visual tree.")]
	public LiveXamlTree XamlTree() => host.ReadXamlTree();
}
