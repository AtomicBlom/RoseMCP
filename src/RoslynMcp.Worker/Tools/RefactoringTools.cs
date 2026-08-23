using System.ComponentModel;

using ModelContextProtocol.Server;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker.Tools;

/// <summary>Changes to the solution. Everything here writes, so everything here reports a diff.</summary>
[McpServerToolType]
public sealed class RefactoringTools(WorkspaceHost host)
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
			(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token),
			cancellationToken);
	}
}
