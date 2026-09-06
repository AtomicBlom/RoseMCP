using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>Reading and changing code, routed to the worker that owns the workspace.</summary>
[McpServerToolType]
public sealed class BrokerAnalysisTools(WorkspaceManager workspaces)
{
	[McpServerTool(
		Name = ToolNames.Diagnostics,
		Title = "Roslyn diagnostics",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.Diagnostics)]
	public Task<DiagnosticsResult> DiagnosticsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DiagnosticScopeArgument)] string? scope = null,
		[Description(ToolDescriptions.DiagnosticTargetArgument)] string? target = null,
		[Description(ToolDescriptions.MinimumSeverityArgument)] string? minimumSeverity = null,
		[Description(ToolDescriptions.IncludeAnalyzersArgument)] bool includeAnalyzers = false,
		[Description(ToolDescriptions.MaxDiagnosticsArgument)] int maxResults = 200,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<DiagnosticsResult>(WorkspaceHints.From(workspace, target), ToolNames.Diagnostics, new()
		{
			["scope"] = scope,
			["target"] = target,
			["minimumSeverity"] = minimumSeverity,
			["includeAnalyzers"] = includeAnalyzers,
			["maxResults"] = maxResults,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.SymbolInfo,
		Title = "Describe a symbol",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.SymbolInfo)]
	public Task<SymbolInfoResult> SymbolInfoAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.IncludeSourceArgument)] bool includeSource = false,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SymbolInfoResult>(WorkspaceHints.From(workspace, filePath), ToolNames.SymbolInfo, new()
		{
			["symbol"] = symbol,
			["filePath"] = filePath,
			["line"] = line,
			["column"] = column,
			["includeSource"] = includeSource,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.FindReferences,
		Title = "Find all references",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FindReferences)]
	public Task<ReferencesResult> FindReferencesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.MaxReferencesArgument)] int maxResults = 200,
		[Description(ToolDescriptions.DefinitionsOnlyArgument)] bool definitionsOnly = false,
		[Description(ToolDescriptions.ReferenceProjectArgument)] string? project = null,
		[Description(ToolDescriptions.IncludePreviewsArgument)] bool includePreviews = true,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<ReferencesResult>(WorkspaceHints.From(workspace, filePath), ToolNames.FindReferences, new()
		{
			["symbol"] = symbol,
			["filePath"] = filePath,
			["line"] = line,
			["column"] = column,
			["maxResults"] = maxResults,
			["definitionsOnly"] = definitionsOnly,
			["project"] = project,
			["includePreviews"] = includePreviews,
		}, cancellationToken, progress);

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
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SymbolSearchResult>(WorkspaceHints.From(workspace), ToolNames.SearchSymbols, new()
		{
			["query"] = query,
			["maxResults"] = maxResults,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.Outline,
		Title = "Outline a type or a file",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.Outline)]
	public Task<OutlineResult> OutlineAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.OutlineTypeArgument)] string? type = null,
		[Description(ToolDescriptions.OutlineFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.IncludeInheritedArgument)] bool includeInherited = false,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<OutlineResult>(WorkspaceHints.From(workspace, filePath), ToolNames.Outline, new()
		{
			["type"] = type,
			["filePath"] = filePath,
			["includeInherited"] = includeInherited,
		}, cancellationToken, progress);

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
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<ProjectGraphResult>(WorkspaceHints.From(workspace), ToolNames.ProjectGraph, new()
		{
			["project"] = project,
		}, cancellationToken, progress);

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
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<NameResolutionResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ResolveName, new()
		{
			["name"] = name,
			["filePath"] = filePath,
			["arity"] = arity,
			["maxResults"] = maxResults,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.ListGeneratedDocuments,
		Title = "List source-generated documents",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ListGeneratedDocuments)]
	public Task<GeneratedDocumentList> ListGeneratedAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<GeneratedDocumentList>(WorkspaceHints.From(workspace), ToolNames.ListGeneratedDocuments, new()
		{
			["project"] = project,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.ReadGeneratedDocument,
		Title = "Read a source-generated document",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ReadGeneratedDocument)]
	public Task<GeneratedDocumentContent> ReadGeneratedAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.HintNameArgument)] string hintName,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<GeneratedDocumentContent>(WorkspaceHints.From(workspace), ToolNames.ReadGeneratedDocument, new()
		{
			["hintName"] = hintName,
			["project"] = project,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.RenameSymbol,
		Title = "Rename a symbol",
		ReadOnly = false,
		Destructive = true,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.RenameSymbol)]
	public Task<RenameResult> RenameSymbolAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.NewNameArgument)] string newName,
		[Description(ToolDescriptions.SymbolArgument)] string? symbol = null,
		[Description(ToolDescriptions.FilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.LineArgument)] int? line = null,
		[Description(ToolDescriptions.ColumnArgument)] int? column = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.RenameOverloadsArgument)] bool renameOverloads = false,
		[Description(ToolDescriptions.RenameInCommentsArgument)] bool renameInComments = false,
		[Description(ToolDescriptions.RenameInStringsArgument)] bool renameInStrings = false,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<RenameResult>(WorkspaceHints.From(workspace, filePath), ToolNames.RenameSymbol, new()
		{
			["symbol"] = symbol,
			["filePath"] = filePath,
			["line"] = line,
			["column"] = column,
			["newName"] = newName,
			["apply"] = apply,
			["renameOverloads"] = renameOverloads,
			["renameInComments"] = renameInComments,
			["renameInStrings"] = renameInStrings,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

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
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<ImplementationsResult>(WorkspaceHints.From(workspace, filePath), ToolNames.FindImplementations, new()
		{
			["symbol"] = symbol,
			["filePath"] = filePath,
			["line"] = line,
			["column"] = column,
			["maxResults"] = maxResults,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.ListCodeFixes,
		Title = "Code fixes available in a file",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ListCodeFixes)]
	public Task<CodeFixList> ListCodeFixesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SingleFilePathArgument)] string filePath,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<CodeFixList>(WorkspaceHints.From(workspace, filePath), ToolNames.ListCodeFixes, new()
		{
			["filePath"] = filePath,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.ApplyCodeFix,
		Title = "Apply a code fix",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ApplyCodeFix)]
	public Task<CodeFixResult> ApplyCodeFixAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DiagnosticIdArgument)] string diagnosticId,
		[Description(ToolDescriptions.FixScopeFilePathArgument)] string filePath,
		[Description(ToolDescriptions.FixScopeArgument)] string scope = "document",
		[Description(ToolDescriptions.FixTitleArgument)] string? fixTitle = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<CodeFixResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ApplyCodeFix, new()
		{
			["diagnosticId"] = diagnosticId,
			["filePath"] = filePath,
			["scope"] = scope,
			["fixTitle"] = fixTitle,
			["apply"] = apply,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.FormatDocuments,
		Title = "Format files the way the repository asks",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FormatDocuments)]
	public Task<FormatResult> FormatAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.FormatFilePathsArgument)] string[] filePaths,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.RemoveUnusedUsingsArgument)] bool removeUnusedUsings = false,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<FormatResult>(WorkspaceHints.From(workspace, filePaths), ToolNames.FormatDocuments, new()
		{
			["filePaths"] = filePaths,
			["apply"] = apply,
			["removeUnusedUsings"] = removeUnusedUsings,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.MoveTypeToFile,
		Title = "Move a type to its own file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.MoveTypeToFile)]
	public Task<MoveTypeResult> MoveTypeToFileAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SplitFilePathArgument)] string filePath,
		[Description(ToolDescriptions.TypeNameArgument)] string typeName,
		[Description(ToolDescriptions.TargetPathArgument)] string? targetPath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MoveTypeResult>(WorkspaceHints.From(workspace, filePath), ToolNames.MoveTypeToFile, new()
		{
			["filePath"] = filePath,
			["typeName"] = typeName,
			["targetPath"] = targetPath,
			["apply"] = apply,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.ReplaceMember,
		Title = "Write over a member",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ReplaceMember)]
	public Task<MemberEditResult> ReplaceMemberAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MemberArgument)] string symbol,
		[Description(ToolDescriptions.DeclarationCodeArgument)] string code,
		[Description(ToolDescriptions.UsingsArgument)] string[]? usings = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ReplaceMember, new()
		{
			["symbol"] = symbol,
			["code"] = code,
			["usings"] = usings,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.ReplaceBody,
		Title = "Write over a member's body",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ReplaceBody)]
	public Task<MemberEditResult> ReplaceBodyAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MemberArgument)] string symbol,
		[Description(ToolDescriptions.BodyCodeArgument)] string? code = null,
		[Description(ToolDescriptions.FindArgument)] string? find = null,
		[Description(ToolDescriptions.ReplaceArgument)] string? replace = null,
		[Description(ToolDescriptions.PositionArgument)] string? position = null,
		[Description(ToolDescriptions.UsingsArgument)] string[]? usings = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ReplaceBody, new()
		{
			["symbol"] = symbol,
			["code"] = code,
			["find"] = find,
			["replace"] = replace,
			["position"] = position,
			["usings"] = usings,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.AddMember,
		Title = "Add members to a type",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.AddMember)]
	public Task<MemberEditResult> AddMemberAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.AddToTypeArgument)] string type,
		[Description(ToolDescriptions.MembersCodeArgument)] string code,
		[Description(ToolDescriptions.UsingsArgument)] string[]? usings = null,
		[Description(ToolDescriptions.AfterArgument)] string? after = null,
		[Description(ToolDescriptions.BeforeArgument)] string? before = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.AddMember, new()
		{
			["type"] = type,
			["code"] = code,
			["usings"] = usings,
			["after"] = after,
			["before"] = before,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.ChangeSignature,
		Title = "Change a member's parameters",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ChangeSignature)]
	public Task<SignatureChangeResult> ChangeSignatureAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MemberArgument)] string symbol,
		[Description(ToolDescriptions.ParametersArgument)] string parameters,
		[Description(ToolDescriptions.ArgumentsArgument)] string[]? arguments = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifySolutionArgument)] bool verify = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SignatureChangeResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ChangeSignature, new()
		{
			["symbol"] = symbol,
			["parameters"] = parameters,
			["arguments"] = arguments,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.BuildFreshness,
		Title = "Is the build output newer than the sources",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.BuildFreshness)]
	public Task<BuildFreshnessReport> BuildFreshnessAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ProjectOrPathFilterArgument)] string? project = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<BuildFreshnessReport>(WorkspaceHints.From(workspace), ToolNames.BuildFreshness, new()
		{
			["project"] = project,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.AddUsing,
		Title = "Import a namespace into a file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.AddUsing)]
	public Task<UsingResult> AddUsingAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SingleFilePathArgument)] string filePath,
		[Description(ToolDescriptions.NamespacesArgument)] string[] namespaces,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<UsingResult>(WorkspaceHints.From(workspace, filePath), ToolNames.AddUsing, new()
		{
			["filePath"] = filePath,
			["namespaces"] = namespaces,
			["apply"] = apply,
			["verify"] = verify,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.MoveMember,
		Title = "Move a member to another type",
		ReadOnly = false,
		Destructive = true,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.MoveMember)]
	public Task<MemberEditResult> MoveMemberAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MoveMemberArgument)] string symbol,
		[Description(ToolDescriptions.TargetTypeArgument)] string targetType,
		[Description(ToolDescriptions.CallSitesArgument)] string callSites = "qualify",
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.MoveMember, new()
		{
			["symbol"] = symbol,
			["targetType"] = targetType,
			["callSites"] = callSites,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.DeleteMember,
		Title = "Remove a member",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.DeleteMember)]
	public Task<MemberEditResult> DeleteMemberAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MemberArgument)] string symbol,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.DeleteMember, new()
		{
			["symbol"] = symbol,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.AddFile,
		Title = "Create a C# file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.AddFile)]
	public Task<AddFileResult> AddFileAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.NewFilePathArgument)] string filePath,
		[Description(ToolDescriptions.FileCodeArgument)] string code,
		[Description(ToolDescriptions.ExtraUsingsArgument)] string[]? usings = null,
		[Description(ToolDescriptions.NewFileProjectArgument)] string? project = null,
		[Description(ToolDescriptions.ResolveUsingsArgument)] bool resolveUsings = true,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<AddFileResult>(WorkspaceHints.From(workspace, filePath), ToolNames.AddFile, new()
		{
			["filePath"] = filePath,
			["code"] = code,
			["usings"] = usings,
			["project"] = project,
			["resolveUsings"] = resolveUsings,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.ReplaceDocComment,
		Title = "Replace a documentation comment",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ReplaceDocComment)]
	public Task<MemberEditResult> ReplaceDocCommentAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DeclarationArgument)] string symbol,
		[Description(ToolDescriptions.CommentArgument)] string comment,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ReplaceDocComment, new()
		{
			["symbol"] = symbol,
			["comment"] = comment,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	[McpServerTool(
		Name = ToolNames.SetAttribute,
		Title = "Add, replace or remove an attribute",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.SetAttribute)]
	public Task<MemberEditResult> SetAttributeAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DeclarationArgument)] string symbol,
		[Description(ToolDescriptions.AttributeArgument)] string attribute,
		[Description(ToolDescriptions.AttributeActionArgument)] string action = "set",
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		[Description(ToolDescriptions.WorkspaceArgument)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.SetAttribute, new()
		{
			["symbol"] = symbol,
			["attribute"] = attribute,
			["action"] = action,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
			["verifyScope"] = verifyScope,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	private Task<T> ForwardAsync<T>(
		WorkspaceHints hints,
		string tool,
		Dictionary<string, object?> arguments,
		CancellationToken cancellationToken,
		IProgress<ProgressNotificationValue> progress,
		bool retryIfWorkerDied = true)
	{
		// A null means "not supplied". Forwarding it would override the worker's own default.
		var supplied = arguments
			.Where(pair => pair.Value is not null)
			.ToDictionary(pair => pair.Key, pair => pair.Value);

		return workspaces.CallAsync<T>(hints, tool, supplied, retryIfWorkerDied, cancellationToken, progress);
	}
}
