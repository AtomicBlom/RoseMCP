using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Semantic navigation, as opposed to guessing from text search.</summary>
[McpServerToolType]
public sealed class NavigationTools(WorkspaceCalls calls)
{
	[McpServerTool(
		Name = ToolNames.FindImplementations,
		Title = "Find implementations, overrides and derived types",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FindImplementations)]
	public Task<ImplementationsResult> FindImplementationsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.MaxImplementationsArgument)] int maxResults = 200,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default)
	{
		var target = new SymbolTarget { Symbol = symbol, FilePath = filePath, Line = line, Column = column };

		// The search says nothing as it goes, but it is long enough on a large solution that the wait
		// keeps only its half of the bar rather than reaching the end before the search has begun.
		return calls.ReadAsync(
			progress,
			(snapshot, _) => NavigationService.FindImplementationsAsync(
				snapshot, target, maxResults <= 0 ? 200 : maxResults, cancellationToken, project),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.SymbolInfo,
		Title = "Describe a symbol",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.SymbolInfo)]
	public Task<ReadBatch<SymbolInfoResult>> SymbolInfoAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SymbolsArgument)] string[]? symbols = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.IncludeSourceArgument)] bool includeSource = false,
		[Description(ToolDescriptions.OutlineMembersArgument)] string? members = null,
		[Description(ToolDescriptions.MaxSymbolMembersArgument)] int maxMembers = OutlineService.DefaultMaxMembers,
		CancellationToken cancellationToken = default)
	{
		var requested = Requested(symbols, filePath, line, column);

		return calls.ReadAsync(
			progress,
			snapshot => ReadBatches.EachAsync(
				snapshot,
				requested,
				(request, used) => NavigationService.DescribeAsync(
					snapshot, Target(symbols, request, filePath, line, column), cancellationToken, includeSource, members, maxMembers, used),
				answer => answer.Members?.Count ?? 0,
				(answer, shared) => answer with { Notices = ReadBatches.Own(answer.Notices, shared) },
				cancellationToken,
				listed: symbols is not null),
			cancellationToken);
	}

	/// <summary>
	/// What a call taking <c>symbols</c> asks about: each name, or the one position it points at. A
	/// position is named in its entry as <c>file:line:column</c>.
	/// </summary>
	private static IReadOnlyList<string> Requested(string[]? symbols, string? filePath, int? line, int? column)
	{
		var pointed = new SymbolTarget { FilePath = filePath, Line = line, Column = column };

		return ReadBatches.Requested(
			symbols,
			"symbols",
			pointed.IsByPosition ? $"{filePath}:{line}:{column}" : null,
			"Name the symbols, as a list of Namespace.Type.Member, or give filePath with line and column. "
				+ "A name needs no position and does not go stale when the file is edited. A local variable or "
				+ "a parameter is declared inside a member rather than as one, so it has no name to give here and "
				+ "needs the position.");
	}

	/// <summary>One request of a call taking <c>symbols</c>, as the target it names.</summary>
	private static SymbolTarget Target(string[]? symbols, string request, string? filePath, int? line, int? column) =>
		symbols is null
			? new SymbolTarget { FilePath = filePath, Line = line, Column = column }
			: new SymbolTarget { Symbol = request, FilePath = filePath };

	[McpServerTool(
		Name = ToolNames.FindReferences,
		Title = "Find all references",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FindReferences)]
	public Task<ReadBatch<ReferencesResult>> FindReferencesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SymbolsArgument)] string[]? symbols = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.MaxReferencesArgument)] int maxResults = 200,
		[Description(ToolDescriptions.DefinitionsOnlyArgument)] bool definitionsOnly = false,
		[Description(ToolDescriptions.ReferenceProjectArgument)] string? project = null,
		[Description(ToolDescriptions.IncludePreviewsArgument)] bool includePreviews = true,
		[Description(ToolDescriptions.ContainingMemberArgument)] string? containingMember = null,
		[Description(ToolDescriptions.IsTestProjectArgument)] bool? isTestProject = null,
		[Description(ToolDescriptions.IsGeneratedArgument)] bool? isGenerated = null,
		CancellationToken cancellationToken = default)
	{
		var requested = Requested(symbols, filePath, line, column);

		return calls.ReadAsync(
			progress,
			(snapshot, working) =>
			{
				// A project no project carries is the call's mistake rather than any one symbol's, so it is
				// refused once, before the search, rather than on every entry.
				if (project is { Length: > 0 }) ProjectNames.Resolve(snapshot.Solution, project);

				return ReadBatches.EachAsync(
					snapshot,
					requested,
					(request, used) =>
					{
						// Reported without a percentage, deliberately. Roslyn's reference search offers no
						// progress and cannot say up front how much of the solution it will visit, so an
						// honest "working on it" beats a number that would be invented here.
						working.Report($"Searching the solution for references to {request}");

						return NavigationService.FindReferencesAsync(
							snapshot,
							Target(symbols, request, filePath, line, column),
							maxResults <= 0 ? 200 : maxResults,
							cancellationToken,
							definitionsOnly,
							project,
							includePreviews,
							containingMember,
							isTestProject,
							isGenerated,
							used);
					},
					answer => answer.Files.Sum(file => file.References.Count),
					(answer, shared) => answer with { Notices = ReadBatches.Own(answer.Notices, shared) },
					cancellationToken,
					listed: symbols is not null);
			},
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.SearchSymbols,
		Title = "Search symbols by name",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.SearchSymbols)]
	public Task<SymbolSearchResult> SearchSymbolsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SearchQueryArgument)] string query,
		[Description(ToolDescriptions.MaxSearchMatchesArgument)] int maxResults = 50,
		[Description(ToolDescriptions.SearchKindArgument)] string? kind = null,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default) =>
		calls.ReadAsync(
			progress,
			(snapshot, working) =>
			{
				working.Report($"Searching declarations for '{query}'");

				return NavigationService.SearchAsync(
					snapshot, query, maxResults <= 0 ? 50 : maxResults, cancellationToken, kind, project);
			},
			cancellationToken);

	[McpServerTool(
		Name = ToolNames.ResolveName,
		Title = "Find the namespace a name needs",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ResolveName)]
	public Task<NameResolutionResult> ResolveNameAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ResolveNameArgument)] string name,
		[Description(ToolDescriptions.ResolveFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ArityArgument)] int? arity = null,
		[Description(ToolDescriptions.MaxCandidatesArgument)] int maxResults = 20,
		CancellationToken cancellationToken = default)
	{
		var request = new ResolveNameRequest
		{
			Name = name,
			FilePath = filePath,
			Arity = arity,
			MaxResults = maxResults <= 0 ? 20 : maxResults,
		};

		return calls.ReadAsync(
			progress,
			(snapshot, working) =>
			{
				working.Report($"Working out what {name} could be");

				return NameResolver.ResolveAsync(snapshot, request, cancellationToken, working);
			},
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.Outline,
		Title = "Outline a type or a file",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.Outline)]
	public Task<ReadBatch<OutlineResult>> OutlineAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.OutlineTypesArgument)] string[]? symbols = null,
		[Description(ToolDescriptions.OutlineFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.OutlineMembersArgument)] string? members = null,
		[Description(ToolDescriptions.MaxOutlineMembersArgument)] int maxMembers = OutlineService.DefaultMaxMembers,
		[Description(ToolDescriptions.IncludeInheritedArgument)] bool includeInherited = false,
		[Description(ToolDescriptions.IncludeDocumentationArgument)] bool includeDocumentation = false,
		[Description(ToolDescriptions.IncludeSignaturesArgument)] bool includeSignatures = false,
		CancellationToken cancellationToken = default)
	{
		var pathed = !string.IsNullOrWhiteSpace(filePath);

		if (symbols is not null && pathed)
		{
			throw new ArgumentException(
				"Name types or give a file path, not both -- they are two ways of choosing what to outline.");
		}

		var requested = ReadBatches.Requested(symbols, "symbols", pathed ? filePath : null, "Name the types, as a list of Namespace.Type, or give a file path.");

		return calls.ReadAsync(
			progress,
			snapshot => ReadBatches.EachAsync(
				snapshot,
				requested,
				(request, used) => OutlineService.OutlineAsync(
					snapshot,
					pathed ? null : request,
					pathed ? request : null,
					includeInherited,
					includeDocumentation,
					includeSignatures,
					cancellationToken,
					members,
					maxMembers,
					used),
				answer => answer.Types.Sum(type => type.Members.Count),
				(answer, shared) => answer with { Notices = ReadBatches.Own(answer.Notices, shared) },
				cancellationToken,
				listed: symbols is not null),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.FindSplitOptions,
		Title = "Where a type could be split",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FindSplitOptions)]
	public Task<IslandsResult> FindSplitOptionsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.OutlineTypeArgument)] string? symbol = null,
		[Description(ToolDescriptions.SplitOptionsFilePathArgument)] string? filePath = null,
		CancellationToken cancellationToken = default) =>
		calls.ReadAsync(
			progress,
			snapshot => IslandService.IslandsAsync(snapshot, symbol, filePath, cancellationToken),
			cancellationToken);

	[McpServerTool(
		Name = ToolNames.ProjectGraph,
		Title = "How the projects depend on each other",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ProjectGraph)]
	public Task<ProjectGraphResult> ProjectGraphAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default) =>
		calls.ReadAsync(
			progress,
			snapshot => Task.FromResult(ProjectGraphService.Describe(snapshot, project)),
			cancellationToken);
}
