using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>Reading and changing code, routed to the worker that owns the workspace.</summary>
[McpServerToolType]
public sealed class BrokerAnalysisTools(WorkspaceManager workspaces)
{
	private const string WorkspaceHelp =
		"Path to a solution, project, or any file inside one. Usually omitted: it is inferred from the "
			+ "other arguments, or from the working directory. Needed only where a directory holds "
			+ "several solutions and none of them is pinned, which is reported when it happens.";

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
		[Description("document, project, or solution. Defaults to solution.")] string? scope = null,
		[Description("File path for document scope, or project name for project scope.")] string? target = null,
		[Description("Lowest severity: hidden, info, warning, or error. Defaults to warning.")] string? minimumSeverity = null,
		[Description("Run analyzers too. Much slower over a whole solution; off by default.")] bool includeAnalyzers = false,
		[Description("Maximum diagnostics to return. Defaults to 200.")] int maxResults = 200,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("The symbol by name, as Namespace.Type.Member. Add a parameter list to pick an overload.")] string? symbol = null,
		[Description("Path to the file. With line and column, or to narrow a name.")] string? filePath = null,
		[Description("One-based line number. Only needed when pointing at a position rather than naming a symbol.")] int? line = null,
		[Description("One-based column, pointing at the identifier itself.")] int? column = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SymbolInfoResult>(WorkspaceHints.From(workspace, filePath), ToolNames.SymbolInfo, new()
		{
			["symbol"] = symbol,
			["filePath"] = filePath,
			["line"] = line,
			["column"] = column,
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
		[Description("Path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("Maximum references to return. Defaults to 200.")] int maxResults = 200,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<ReferencesResult>(WorkspaceHints.From(workspace, filePath), ToolNames.FindReferences, new()
		{
			["filePath"] = filePath,
			["line"] = line,
			["column"] = column,
			["maxResults"] = maxResults,
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
		[Description("Name or abbreviation to search for.")] string query,
		[Description("Maximum matches to return. Defaults to 50.")] int maxResults = 50,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SymbolSearchResult>(WorkspaceHints.From(workspace), ToolNames.SearchSymbols, new()
		{
			["query"] = query,
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
		[Description("Limit to one project by name. Defaults to the whole solution.")] string? project = null,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("Hint name, for example Widget.Greeting.g.cs.")] string hintName,
		[Description("Limit to one project by name.")] string? project = null,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("Path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("The new name.")] string newName,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Also rename overloads of the same method.")] bool renameOverloads = false,
		[Description("Also rename occurrences inside comments.")] bool renameInComments = false,
		[Description("Also rename occurrences inside string literals.")] bool renameInStrings = false,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<RenameResult>(WorkspaceHints.From(workspace, filePath), ToolNames.RenameSymbol, new()
		{
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
		[Description("Path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("Maximum matches to return. Defaults to 200.")] int maxResults = 200,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<ImplementationsResult>(WorkspaceHints.From(workspace, filePath), ToolNames.FindImplementations, new()
		{
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
		[Description("Path to the file.")] string filePath,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("The diagnostic id to fix, for example CA1822.")] string diagnosticId,
		[Description("A file in the scope to fix.")] string filePath,
		[Description("document, project, or solution. Defaults to document.")] string scope = "document",
		[Description("Which fix, when the diagnostic offers several. Matched against the fix titles.")] string? fixTitle = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("Paths of the files to format.")] string[] filePaths,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Also drop using directives the file does not need. Off by default.")] bool removeUnusedUsings = false,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("Path to the file to split.")] string filePath,
		[Description("Name of the type to move out, without type parameters.")] string typeName,
		[Description("Where to put it. Defaults to <typeName>.cs beside the source file.")] string? targetPath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
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
		[Description("The member, as Namespace.Type.Member. Add a parameter list to pick an overload.")] string symbol,
		[Description("The whole declaration, attributes and documentation comment included.")] string code,
		[Description("Which file, when the name is declared in more than one -- a partial type or member.")] string? filePath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Compile afterwards and report what the edit broke. Defaults to true.")] bool verify = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ReplaceMember, new()
		{
			["symbol"] = symbol,
			["code"] = code,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
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
		[Description("The member, as Namespace.Type.Member. Add a parameter list to pick an overload.")] string symbol,
		[Description("The body: statements, a block in braces, or => expression;.")] string code,
		[Description("Which file, when the name is declared in more than one -- a partial type or member.")] string? filePath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Compile afterwards and report what the edit broke. Defaults to true.")] bool verify = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.ReplaceBody, new()
		{
			["symbol"] = symbol,
			["code"] = code,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
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
		[Description("The type to add to, as Namespace.Type.")] string type,
		[Description("One or more whole declarations.")] string code,
		[Description("Put them after this member, by name.")] string? after = null,
		[Description("Put them before this member, by name.")] string? before = null,
		[Description("Which file, when the type is partial and declared in more than one.")] string? filePath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Compile afterwards and report what the edit broke. Defaults to true.")] bool verify = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MemberEditResult>(WorkspaceHints.From(workspace, filePath), ToolNames.AddMember, new()
		{
			["type"] = type,
			["code"] = code,
			["after"] = after,
			["before"] = before,
			["filePath"] = filePath,
			["apply"] = apply,
			["verify"] = verify,
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
		[Description("The member, as Namespace.Type.Member. Add a parameter list to pick an overload.")] string symbol,
		[Description("The parameters it should have, written as they would go between the parentheses.")] string parameters,
		[Description("What to pass at existing call sites for a new parameter with no default, as name=expression.")] string[]? arguments = null,
		[Description("Which file, when the member is declared in more than one -- a partial.")] string? filePath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Compile the solution afterwards and report what the change broke. Defaults to true.")] bool verify = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
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
