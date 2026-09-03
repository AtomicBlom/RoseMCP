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
		Name = ToolNames.FindImplementations,
		Title = "Find implementations, overrides and derived types",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FindImplementations)]
	public async Task<ImplementationsResult> FindImplementationsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Absolute or solution-relative path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("Maximum matches to return. Defaults to 200.")] int maxResults = 200,
		CancellationToken cancellationToken = default)
	{
		var (waiting, _) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);

		return await NavigationService.FindImplementationsAsync(
			snapshot, filePath, line, column, maxResults <= 0 ? 200 : maxResults, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.SymbolInfo,
		Title = "Describe a symbol",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.SymbolInfo)]
	public async Task<SymbolInfoResult> SymbolInfoAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("The symbol by name, as Namespace.Type.Member. Add a parameter list to pick an overload.")] string? symbol = null,
		[Description("Absolute or solution-relative path to the file. With line and column, or to narrow a name.")] string? filePath = null,
		[Description("One-based line number. Only needed when pointing at a position rather than naming a symbol.")] int? line = null,
		[Description("One-based column, pointing at the identifier itself.")] int? column = null,
		CancellationToken cancellationToken = default)
	{
		// Describing one symbol is instant. The only wait worth reporting is the workspace itself,
		// which on a cold start is the difference between an answer in milliseconds and in minutes.
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);

		return await NavigationService.DescribeAsync(
			snapshot,
			new SymbolInfoRequest { Symbol = symbol, FilePath = filePath, Line = line, Column = column },
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.FindReferences,
		Title = "Find all references",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FindReferences)]
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
	[Description(ToolDescriptions.SearchSymbols)]
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
