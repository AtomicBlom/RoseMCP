using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Semantic navigation, as opposed to guessing from text search.</summary>
[McpServerToolType]
public sealed class NavigationTools(WorkspaceHost host, SharedWorkProgress sharedWork)
{
	[McpServerTool(
		Name = ToolNames.SymbolInfo,
		Title = "Describe the symbol at a position",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        What the symbol at a file position actually is: full signature, kind, accessibility,
        containing type, XML documentation, and every declaration site. Works from a use site as
        well as a declaration. isFromSource being false means the symbol lives in metadata and
        cannot be renamed or edited.
        """)]
	public async Task<SymbolInfoResult> SymbolInfoAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Absolute or solution-relative path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		CancellationToken cancellationToken = default)
	{
		// Describing one symbol is instant. The only wait worth reporting is the workspace itself,
		// which on a cold start is the difference between an answer in milliseconds and in minutes.
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);
		return await NavigationService.DescribeAsync(snapshot, filePath, line, column, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.FindReferences,
		Title = "Find all references",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Every reference to the symbol at a file position, resolved semantically across the whole
        solution. Unlike a text search this follows overrides, interface implementations and
        aliases, and will not match unrelated identifiers that happen to share a name.
        """)]
	public async Task<ReferencesResult> FindReferencesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Absolute or solution-relative path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("Maximum references to return. Defaults to 200.")] int maxResults = 200,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);

		// Reported without a percentage, deliberately. Roslyn's reference search offers no progress
		// and cannot say up front how much of the solution it will visit, so an honest "working on
		// it" beats a number that would be invented here.
		working.Report($"Searching the solution for references to {Path.GetFileName(filePath)}:{line}");

		return await NavigationService.FindReferencesAsync(
			snapshot, filePath, line, column, maxResults <= 0 ? 200 : maxResults, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.SearchSymbols,
		Title = "Search symbols by name",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Finds declarations across the solution by name pattern. Understands the abbreviations people
        actually type, so SLoader matches SolutionLoader. Use this to locate a type or member before
        asking for its references or renaming it.
        """)]
	public async Task<SymbolSearchResult> SearchSymbolsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Name or abbreviation to search for.")] string query,
		[Description("Maximum matches to return. Defaults to 50.")] int maxResults = 50,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);
		working.Report($"Searching declarations for '{query}'");

		return await NavigationService.SearchAsync(snapshot, query, maxResults <= 0 ? 50 : maxResults, cancellationToken);
	}
}
