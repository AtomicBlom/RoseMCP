using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Changes to the solution. Everything here writes, so everything here reports a diff.</summary>
[McpServerToolType]
public sealed class RefactoringTools(WorkspaceHost host, SharedWorkProgress sharedWork)
{
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
        together. Returns a unified diff of every file it changed. Conflicts -- where the new name
        would bind to something else or shadow an existing member -- are reported rather than
        silently applied. Pass apply=false to preview without writing.
        """)]
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
		Name = ToolNames.MoveTypeToFile,
		Title = "Move a type to its own file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Moves one top-level type out of a file that declares several, into a file named after it.
        The declaration goes across with its doc comments and attributes, indented and spaced
        exactly as it was, and using directives that the split makes unnecessary are dropped from
        both files -- which is what stops the result from failing a build that treats unused usings
        as errors. Returns a unified diff of both files. Pass apply=false to preview.
        Declines rather than guessing when the type is the only one in its file, when the target
        already exists, or when preprocessor directives are involved.
        """)]
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
