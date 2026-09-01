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
		"Path to a solution, project, or any file inside one. Optional when exactly one workspace is open.";

	[McpServerTool(
		Name = ToolNames.Diagnostics,
		Title = "Roslyn diagnostics",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Compiler (and optionally analyzer) diagnostics from a live Roslyn compilation, always
        computed against the current state of disk -- edits made by other tools are absorbed before
        the analysis runs, so results are never stale. Diagnostics inside source-generated code are
        included and tagged with the hint name that reads that code back.
        """)]
	public Task<DiagnosticsResult> DiagnosticsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("document, project, or solution. Defaults to solution.")] string? scope = null,
		[Description("File path for document scope, or project name for project scope.")] string? target = null,
		[Description("Lowest severity: hidden, info, warning, or error. Defaults to warning.")] string? minimumSeverity = null,
		[Description("Run analyzers too. Much slower over a whole solution; off by default.")] bool includeAnalyzers = false,
		[Description("Maximum diagnostics to return. Defaults to 200.")] int maxResults = 200,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<DiagnosticsResult>(workspace ?? target, ToolNames.Diagnostics, new()
		{
			["scope"] = scope,
			["target"] = target,
			["minimumSeverity"] = minimumSeverity,
			["includeAnalyzers"] = includeAnalyzers,
			["maxResults"] = maxResults,
		}, cancellationToken, progress);

	[McpServerTool(
		Name = ToolNames.SymbolInfo,
		Title = "Describe the symbol at a position",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        What the symbol at a file position actually is: full signature, kind, accessibility,
        containing type, documentation, and every declaration site. Works from a use site as well as
        a declaration. isFromSource being false means it lives in metadata and cannot be renamed.
        """)]
	public Task<SymbolInfoResult> SymbolInfoAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SymbolInfoResult>(workspace ?? filePath, ToolNames.SymbolInfo, new()
		{
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
	[Description("""
        Every reference to the symbol at a file position, resolved semantically across the whole
        solution. Unlike a text search this follows overrides, interface implementations and
        aliases, and will not match unrelated identifiers that share a name.
        """)]
	public Task<ReferencesResult> FindReferencesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("Maximum references to return. Defaults to 200.")] int maxResults = 200,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<ReferencesResult>(workspace ?? filePath, ToolNames.FindReferences, new()
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
	[Description("""
        Finds declarations across the solution by name pattern, understanding the abbreviations
        people type -- SLoader matches SolutionLoader. Use this to locate a type or member before
        asking for its references or renaming it.
        """)]
	public Task<SymbolSearchResult> SearchSymbolsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Name or abbreviation to search for.")] string query,
		[Description("Maximum matches to return. Defaults to 50.")] int maxResults = 50,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<SymbolSearchResult>(workspace, ToolNames.SearchSymbols, new()
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
	[Description("""
        Lists the documents this solution's source generators produce. These exist only inside the
        compilation and are never written to disk, so no file search will find them. An empty list
        comes with a notice saying whether there are no generators or the generators failed to load.
        """)]
	public Task<GeneratedDocumentList> ListGeneratedAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Limit to one project by name. Defaults to the whole solution.")] string? project = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<GeneratedDocumentList>(workspace, ToolNames.ListGeneratedDocuments, new()
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
	[Description("""
        Returns the full text of one source-generated document, by the hint name from
        rose_list_generated_documents or from a diagnostic's generatedHintName. Use this whenever
        a diagnostic points at a file that does not exist on disk.
        """)]
	public Task<GeneratedDocumentContent> ReadGeneratedAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Hint name, for example Widget.Greeting.g.cs.")] string hintName,
		[Description("Limit to one project by name.")] string? project = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<GeneratedDocumentContent>(workspace, ToolNames.ReadGeneratedDocument, new()
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
	[Description("""
        Renames the symbol at a file position everywhere it is used, using Roslyn's renamer, so
        overrides, interface implementations, partial declarations and cref references all move
        together. Returns a unified diff of every file changed. Conflicts are reported rather than
        silently applied. Pass apply=false to preview without writing.
        """)]
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
		ForwardAsync<RenameResult>(workspace ?? filePath, ToolNames.RenameSymbol, new()
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
		Name = ToolNames.ListCodeFixes,
		Title = "Code fixes available in a file",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        What the solution's own analyzers offer to fix in one file: the diagnostic, the titles of the
        fixes available for it, and whether it can be fixed across a whole project or solution at
        once. Diagnostic ids nothing can fix are listed separately, so an empty answer is not mistaken
        for clean code.
        """)]
	public Task<CodeFixList> ListCodeFixesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Path to the file.")] string filePath,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<CodeFixList>(workspace ?? filePath, ToolNames.ListCodeFixes, new()
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
	[Description("""
        Applies the fix an analyzer ships for one diagnostic id, to a file, a project, or the whole
        solution at once, through Roslyn's own fix-all. Use this rather than editing each occurrence
        by hand: the same rule across fifty files is where hand-fixing and find-and-replace go wrong.
        Only the analyzers that report the requested id are run, so fixing one rule costs a fraction
        of a full analyzer pass. Returns a unified diff; pass apply=false to preview.
        """)]
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
		ForwardAsync<CodeFixResult>(workspace ?? filePath, ToolNames.ApplyCodeFix, new()
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
	[Description("""
        Formats C# files to their own repository's .editorconfig: indentation, brace placement, line
        endings, trailing whitespace and the final newline. Call this after writing or editing a C#
        file by any other means -- hand-written C# routinely lands with spaces where the repository
        wants tabs and LF where it wants CRLF, and where IDE0055 is an error that is a failed build
        rather than untidiness. Returns a unified diff; pass apply=false to check without writing.
        """)]
	public Task<FormatResult> FormatAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Paths of the files to format.")] string[] filePaths,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Also drop using directives the file does not need. Off by default.")] bool removeUnusedUsings = false,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<FormatResult>(workspace ?? filePaths.FirstOrDefault(), ToolNames.FormatDocuments, new()
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
	[Description("""
        Moves one top-level type out of a file that declares several, into a file named after it.
        The declaration goes across with its doc comments and attributes, formatted exactly as it
        was, and using directives the split makes unnecessary are dropped from both files. Returns a
        unified diff of both files; pass apply=false to preview. Use this rather than reading a file
        and writing two, which loses formatting and leaves usings behind that fail a build.
        """)]
	public Task<MoveTypeResult> MoveTypeToFileAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Path to the file to split.")] string filePath,
		[Description("Name of the type to move out, without type parameters.")] string typeName,
		[Description("Where to put it. Defaults to <typeName>.cs beside the source file.")] string? targetPath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default) =>
		ForwardAsync<MoveTypeResult>(workspace ?? filePath, ToolNames.MoveTypeToFile, new()
		{
			["filePath"] = filePath,
			["typeName"] = typeName,
			["targetPath"] = targetPath,
			["apply"] = apply,
			["expectedRevision"] = expectedRevision,
		}, cancellationToken, progress, retryIfWorkerDied: false);

	private Task<T> ForwardAsync<T>(
		string? workspace,
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

		return workspaces.CallAsync<T>(workspace, tool, supplied, retryIfWorkerDied, cancellationToken, progress);
	}
}
