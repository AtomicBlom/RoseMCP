using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Changes to the solution. Everything here writes, so everything here reports a diff.</summary>
[McpServerToolType]
public sealed class RefactoringTools(
	WorkspaceHost host,
	CodeFixCatalog codeFixes,
	DiagnosticsService diagnostics,
	SharedWorkProgress sharedWork)
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
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.Replace,
				Symbol = symbol,
				Code = code,
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				ExpectedRevision = expectedRevision,
			},
			cancellationToken);

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
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = symbol,
				Code = code,
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				ExpectedRevision = expectedRevision,
			},
			cancellationToken);

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
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.Add,
				Symbol = type,
				Code = code,
				After = after,
				Before = before,
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				ExpectedRevision = expectedRevision,
			},
			cancellationToken);

	/// <summary>
	/// The three write-by-symbol tools differ only in their request, so they share everything else:
	/// the same progress split, the same session, and the same ordering behind every pending
	/// mutation and the disk barrier.
	/// </summary>
	private async Task<MemberEditResult> EditAsync(
		IProgress<ProgressNotificationValue> progress,
		MemberEditRequest request,
		CancellationToken cancellationToken)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		return await session.MutateAsync(
			(snapshot, token) => MemberEditService.EditAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}
}
