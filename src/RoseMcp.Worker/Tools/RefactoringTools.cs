using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Changes to the solution. Everything here writes, so everything here reports a diff.</summary>
[McpServerToolType]
public sealed class RefactoringTools(WorkspaceHost host, CodeFixCatalog codeFixes, SharedWorkProgress sharedWork)
{
	[McpServerTool(
		Name = ToolNames.ApplyCodeFix,
		Title = "Apply a code fix",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ApplyCodeFix)]
	public async Task<CodeFixResult> ApplyCodeFixAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("The diagnostic id to fix, for example CA1822.")] string diagnosticId,
		[Description("A file in the scope to fix; the fix has to start somewhere.")] string filePath,
		[Description("document, project, or solution. Defaults to document.")] string scope = "document",
		[Description("Which fix, when the diagnostic offers several. Matched against the fix titles.")] string? fixTitle = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new CodeFixRequest
		{
			DiagnosticId = diagnosticId,
			FilePath = filePath,
			Scope = scope,
			FixTitle = fixTitle,
			Apply = apply,
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => CodeFixService.ApplyAsync(
				snapshot, codeFixes, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.RenameSymbol,
		Title = "Rename a symbol",
		ReadOnly = false,
		Destructive = true,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.RenameSymbol)]
	public async Task<RenameResult> RenameSymbolAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Absolute or solution-relative path to the file.")] string filePath,
		[Description("One-based line number.")] int line,
		[Description("One-based column, pointing at the identifier itself.")] int column,
		[Description("The new name.")] string newName,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Also rename overloads of the same method.")] bool renameOverloads = false,
		[Description("Also rename occurrences inside comments.")] bool renameInComments = false,
		[Description("Also rename occurrences inside string literals.")] bool renameInStrings = false,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		// A rename is the longest thing a client can ask for and the only one that writes, so it is
		// the call most worth watching. The wait covers the queue behind other mutations as well as
		// the workspace itself: a rename is ordered behind every request already in flight.
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new RenameRequest
		{
			FilePath = filePath,
			Line = line,
			Column = column,
			NewName = newName,
			Apply = apply,
			RenameOverloads = renameOverloads,
			RenameInComments = renameInComments,
			RenameInStrings = renameInStrings,
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.FormatDocuments,
		Title = "Format files the way the repository asks",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.FormatDocuments)]
	public async Task<FormatResult> FormatAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Absolute or solution-relative paths of the files to format.")] string[] filePaths,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Also drop using directives the file does not need. Off by default.")] bool removeUnusedUsings = false,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new FormatRequest
		{
			FilePaths = filePaths,
			Apply = apply,
			RemoveUnusedUsings = removeUnusedUsings,
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => FormatService.FormatAsync(snapshot, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.MoveTypeToFile,
		Title = "Move a type to its own file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.MoveTypeToFile)]
	public async Task<MoveTypeResult> MoveTypeToFileAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Absolute or solution-relative path to the file to split.")] string filePath,
		[Description("Name of the type to move out, without type parameters.")] string typeName,
		[Description("Where to put it. Defaults to <typeName>.cs beside the source file.")] string? targetPath = null,
		[Description("Write the change. False returns the diff without touching disk. Defaults to true.")] bool apply = true,
		[Description("Fail rather than apply if the workspace has moved past this revision.")] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new MoveTypeRequest
		{
			FilePath = filePath,
			TypeName = typeName,
			TargetPath = targetPath,
			Apply = apply,
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => MoveTypeService.MoveAsync(snapshot, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}
}
