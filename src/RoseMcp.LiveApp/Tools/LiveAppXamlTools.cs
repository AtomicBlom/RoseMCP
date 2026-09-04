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
		[Description("Root the tree at this named element's subtree.")] string? rootName = null,
		[Description("Skip this many nodes (paging).")] int offset = 0,
		[Description("Return at most this many nodes; 0 for all.")] int limit = 0)
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
		[Description("The element handle from a tree snapshot.")] ulong handle,
		[Description("Include framework default values, not only set ones.")] bool includeDefaults = false)
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
		[Description("The XAML file to apply what is now on disk from; the session tracks what it last sent.")]
		string? filePath = null,
		[Description("The previous XAML. Not needed with filePath after the first apply.")] string? oldXaml = null,
		[Description("The new XAML to apply, for markup that is not on disk.")] string? newXaml = null)
		=> host.ApplyXaml(oldXaml, newXaml, filePath);

	[McpServerTool(
		Name = ToolNames.LiveAppXamlSelectMode,
		Title = "Live-app XAML select mode",
		ReadOnly = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Arm the interactive selection overlay so the next click in the app picks that element.")]
	public LiveXamlSelection XamlSelectMode(
		[Description("Include elements the framework would not hit-test. Off by default.")]
		bool includeAllElements = false,
		[Description("Prefer the element declared in the app's own markup over a control template's parts.")]
		bool justMyXaml = true)
		=> host.EnterXamlSelectMode(includeAllElements, justMyXaml);

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
	public LiveXamlSelection XamlSelectElement(ulong handle) => host.SelectXamlElement(handle);
}
