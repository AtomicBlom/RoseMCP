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

	[McpServerTool(
		Name = ToolNames.LiveAppXamlProperties,
		Title = "Live-app XAML element properties",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Read one element's XAML properties (by handle) with provenance and source location.")]
	public LiveXamlProperties XamlProperties(
		[Description("The element handle from a tree snapshot.")] ulong handle,
		[Description("Include framework default values, not only set ones.")] bool includeDefaults = false)
		=> host.ReadXamlProperties(handle, includeDefaults);
}
