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
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.MaxImplementationsArgument)] int maxResults = 200,
		CancellationToken cancellationToken = default)
	{
		var (waiting, _) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);

		var target = new SymbolTarget { Symbol = symbol, FilePath = filePath, Line = line, Column = column };

		return await NavigationService.FindImplementationsAsync(
			snapshot, target, maxResults <= 0 ? 200 : maxResults, cancellationToken);
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
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.IncludeSourceArgument)] bool includeSource = false,
		CancellationToken cancellationToken = default)
	{
		// Describing one symbol is instant. The only wait worth reporting is the workspace itself,
		// which on a cold start is the difference between an answer in milliseconds and in minutes.
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);

		return await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = symbol, FilePath = filePath, Line = line, Column = column },
			cancellationToken,
			includeSource);
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
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.MaxReferencesArgument)] int maxResults = 200,
		[Description(ToolDescriptions.DefinitionsOnlyArgument)] bool definitionsOnly = false,
		[Description(ToolDescriptions.ReferenceProjectArgument)] string? project = null,
		[Description(ToolDescriptions.IncludePreviewsArgument)] bool includePreviews = true,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);

		var target = new SymbolTarget { Symbol = symbol, FilePath = filePath, Line = line, Column = column };

		// Reported without a percentage, deliberately. Roslyn's reference search offers no progress
		// and cannot say up front how much of the solution it will visit, so an honest "working on
		// it" beats a number that would be invented here.
		working.Report($"Searching the solution for references to {target.Describe()}");

		return await NavigationService.FindReferencesAsync(
			snapshot,
			target,
			maxResults <= 0 ? 200 : maxResults,
			cancellationToken,
			definitionsOnly,
			project,
			includePreviews);
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
		[Description(ToolDescriptions.SearchQueryArgument)] string query,
		[Description(ToolDescriptions.MaxSearchMatchesArgument)] int maxResults = 50,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);
		working.Report($"Searching declarations for '{query}'");

		return await NavigationService.SearchAsync(snapshot, query, maxResults <= 0 ? 50 : maxResults, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.ResolveName,
		Title = "Find the namespace a name needs",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ResolveName)]
	public async Task<NameResolutionResult> ResolveNameAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ResolveNameArgument)] string name,
		[Description(ToolDescriptions.ResolveFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ArityArgument)] int? arity = null,
		[Description(ToolDescriptions.MaxCandidatesArgument)] int maxResults = 20,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);
		working.Report($"Working out what {name} could be");

		var request = new ResolveNameRequest
		{
			Name = name,
			FilePath = filePath,
			Arity = arity,
			MaxResults = maxResults <= 0 ? 20 : maxResults,
		};

		return await NameResolver.ResolveAsync(snapshot, request, cancellationToken, working);
	}

	[McpServerTool(
		Name = ToolNames.Outline,
		Title = "Outline a type or a file",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.Outline)]
	public async Task<OutlineResult> OutlineAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.OutlineTypeArgument)] string? symbol = null,
		[Description(ToolDescriptions.OutlineFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.IncludeInheritedArgument)] bool includeInherited = false,
		[Description(ToolDescriptions.IncludeDocumentationArgument)] bool includeDocumentation = true,
		[Description(ToolDescriptions.IncludeSignaturesArgument)] bool includeSignatures = true,
		CancellationToken cancellationToken = default)
	{
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);

		return await OutlineService.OutlineAsync(
			snapshot,
			symbol,
			filePath,
			includeInherited,
			includeDocumentation,
			includeSignatures,
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.ProjectGraph,
		Title = "How the projects depend on each other",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ProjectGraph)]
	public async Task<ProjectGraphResult> ProjectGraphAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default)
	{
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);

		return ProjectGraphService.Describe(snapshot, project);
	}
}
