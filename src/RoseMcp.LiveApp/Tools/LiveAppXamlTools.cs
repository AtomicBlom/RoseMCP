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
	public LiveXamlTree XamlTree(
		[Description(ToolDescriptions.XamlRootNameArgument)] string? rootName = null,
		[Description(ToolDescriptions.XamlOffsetArgument)] int offset = 0,
		[Description(ToolDescriptions.XamlLimitArgument)] int limit = 0)
		=> host.ReadXamlTree(rootName, offset, limit);

	[McpServerTool(
		Name = ToolNames.LiveAppXamlProperties,
		Title = "Live-app XAML element properties",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Read one element's XAML properties (by handle) with provenance and source location.")]
	public LiveXamlProperties XamlProperties(
		[Description(ToolDescriptions.XamlHandleArgument)] ulong handle,
		[Description(ToolDescriptions.IncludeDefaultsArgument)] bool includeDefaults = false)
		=> host.ReadXamlProperties(handle, includeDefaults);

	[McpServerTool(
		Name = ToolNames.LiveAppXamlApply,
		Title = "Live-app XAML edit",
		ReadOnly = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Apply a XAML change to the live visual tree, from a file the session tracks or from two "
			+ "versions of the markup.")]
	public LiveXamlApplyResult XamlApply(
		[Description(ToolDescriptions.XamlFilePathArgument)]
		string? filePath = null,
		[Description(ToolDescriptions.XamlOldMarkupArgument)] string? oldXaml = null,
		[Description(ToolDescriptions.XamlNewMarkupArgument)] string? newXaml = null)
		=> host.ApplyXaml(oldXaml, newXaml, filePath);

	[McpServerTool(
		Name = ToolNames.LiveAppXamlSelectMode,
		Title = "Live-app XAML select mode",
		ReadOnly = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Arm the interactive selection overlay so the next click in the app picks that element, or disarm it.")]
	public LiveXamlSelection XamlSelectMode(
		[Description(ToolDescriptions.IncludeAllElementsArgument)]
		bool includeAllElements = false,
		[Description(ToolDescriptions.JustMyXamlArgument)]
		bool justMyXaml = true,
		[Description(ToolDescriptions.ArmArgument)]
		bool arm = true)
		=> host.EnterXamlSelectMode(includeAllElements, justMyXaml, arm);

	[McpServerTool(
		Name = ToolNames.LiveAppXamlSelection,
		Title = "Live-app XAML selection",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Read the element the user picked by clicking it in the running app.")]
	public LiveXamlSelection XamlSelection() => host.ReadXamlSelection();

	[McpServerTool(
		Name = ToolNames.LiveAppXamlDeselect,
		Title = "Live-app XAML deselect",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Clear the picked element and the mark drawn over the app.")]
	public LiveXamlSelection XamlDeselect() => host.ClearXamlSelection();

	[McpServerTool(
		Name = ToolNames.LiveAppXamlSelectElement,
		Title = "Live-app XAML select by handle",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Select the element a handle names, without a click.")]
	public LiveXamlSelection XamlSelectElement(
		[Description(ToolDescriptions.XamlHandleArgument)] ulong handle)
		=> host.SelectXamlElement(handle);
}
