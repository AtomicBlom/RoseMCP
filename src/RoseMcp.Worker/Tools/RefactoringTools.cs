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
		[Description(ToolDescriptions.DiagnosticIdArgument)] string diagnosticId,
		[Description(ToolDescriptions.FixScopeFilePathArgument)] string filePath,
		[Description(ToolDescriptions.FixScopeArgument)] string scope = "document",
		[Description(ToolDescriptions.FixTitleArgument)] string? fixTitle = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
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
			Target = new SymbolTarget { Symbol = symbol, FilePath = filePath, Line = line, Column = column },
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
		[Description(ToolDescriptions.FormatFilePathsArgument)] string[] filePaths,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.RemoveUnusedUsingsArgument)] bool removeUnusedUsings = false,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
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
		[Description(ToolDescriptions.SplitFilePathArgument)] string filePath,
		[Description(ToolDescriptions.MovedTypeArgument)] string symbol,
		[Description(ToolDescriptions.TargetPathArgument)] string? targetPath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new MoveTypeRequest
		{
			FilePath = filePath,
			Symbol = symbol,
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
		[Description(ToolDescriptions.MemberArgument)] string symbol,
		[Description(ToolDescriptions.DeclarationCodeArgument)] string code,
		[Description(ToolDescriptions.UsingsArgument)] string[]? usings = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.Replace,
				Symbol = symbol,
				Code = code,
				Usings = usings ?? [],
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				VerifyScope = ScopeOf(verifyScope),
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
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = symbol,
				Code = code ?? string.Empty,
				Find = find,
				Replace = replace,
				Position = PositionOf(position),
				Usings = usings ?? [],
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				VerifyScope = ScopeOf(verifyScope),
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
		[Description(ToolDescriptions.AddToTypeArgument)] string symbol,
		[Description(ToolDescriptions.MembersCodeArgument)] string code,
		[Description(ToolDescriptions.UsingsArgument)] string[]? usings = null,
		[Description(ToolDescriptions.AfterArgument)] string? after = null,
		[Description(ToolDescriptions.BeforeArgument)] string? before = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.Add,
				Symbol = symbol,
				Code = code,
				Usings = usings ?? [],
				After = after,
				Before = before,
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				VerifyScope = ScopeOf(verifyScope),
				ExpectedRevision = expectedRevision,
			},
			cancellationToken);

	[McpServerTool(
		Name = ToolNames.ChangeSignature,
		Title = "Change a member's parameters",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ChangeSignature)]
	public async Task<SignatureChangeResult> ChangeSignatureAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MemberArgument)] string symbol,
		[Description(ToolDescriptions.ParametersArgument)] string parameters,
		[Description(ToolDescriptions.ArgumentsArgument)] string[]? arguments = null,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifySolutionArgument)] bool verify = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		// The longest of the write operations by some distance: it finds every reference in the
		// solution and then compiles all of it, so the wait is worth reporting.
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new ChangeSignatureRequest
		{
			Symbol = symbol,
			Parameters = parameters,
			Arguments = arguments ?? [],
			FilePath = filePath,
			Apply = apply,
			Verify = verify,
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => ChangeSignatureService.ChangeAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.AddUsing,
		Title = "Import a namespace into a file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.AddUsing)]
	public async Task<UsingResult> AddUsingAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SingleFilePathArgument)] string filePath,
		[Description(ToolDescriptions.NamespacesArgument)] string[] namespaces,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new AddUsingRequest
		{
			FilePath = filePath,
			Namespaces = namespaces,
			Apply = apply,
			Verify = verify,
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => AddUsingService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

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

	[McpServerTool(
		Name = ToolNames.MoveMember,
		Title = "Move a member to another type",
		ReadOnly = false,
		Destructive = true,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.MoveMember)]
	public async Task<MemberEditResult> MoveMemberAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.MoveMemberArgument)] string symbol,
		[Description(ToolDescriptions.TargetTypeArgument)] string targetType,
		[Description(ToolDescriptions.CallSitesArgument)] string callSites = "qualify",
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var request = new MoveMemberRequest
		{
			Symbol = symbol,
			TargetType = targetType,
			CallSites = StyleOf(callSites),
			FilePath = filePath,
			Apply = apply,
			Verify = verify,
			VerifyScope = ScopeOf(verifyScope),
			ExpectedRevision = expectedRevision,
		};

		return await RunAsync(
			progress,
			(session, snapshot, working, token) => MoveMemberService.MoveAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	/// <summary>
	/// What the caller asked to happen to the call sites. An unrecognised name is refused rather than
	/// taken as either: the two produce different files, and guessing would produce the one they did
	/// not ask for.
	/// </summary>
	private static CallSiteStyle StyleOf(string? requested)
	{
		if (string.IsNullOrWhiteSpace(requested)) return CallSiteStyle.Qualify;

		if (Enum.TryParse<CallSiteStyle>(requested, ignoreCase: true, out var style)) return style;

		throw new ArgumentException($"'{requested}' is not a call-site style. Use qualify or usingStatic.");
	}

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
		CancellationToken cancellationToken = default) =>
		EditAsync(
			progress,
			new MemberEditRequest
			{
				Kind = MemberEditKind.Delete,
				Symbol = symbol,
				FilePath = filePath,
				Apply = apply,
				Verify = verify,
				VerifyScope = ScopeOf(verifyScope),
				ExpectedRevision = expectedRevision,
			},
			cancellationToken);

	[McpServerTool(
		Name = ToolNames.AddFile,
		Title = "Create a C# file",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.AddFile)]
	public async Task<AddFileResult> AddFileAsync(
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
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		var request = new AddFileRequest
		{
			FilePath = filePath,
			Code = code,
			Usings = usings ?? [],
			Project = project,
			ResolveUsings = resolveUsings,
			Apply = apply,
			Verify = verify,
			VerifyScope = ScopeOf(verifyScope),
			ExpectedRevision = expectedRevision,
		};

		return await session.MutateAsync(
			(snapshot, token) => AddFileService.AddAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.ReplaceDocComment,
		Title = "Replace a documentation comment",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ReplaceDocComment)]
	public async Task<MemberEditResult> ReplaceDocCommentAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DeclarationArgument)] string symbol,
		[Description(ToolDescriptions.CommentArgument)] string comment,
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var request = new DeclarationEditRequest
		{
			Symbol = symbol,
			Comment = comment,
			FilePath = filePath,
			Apply = apply,
			Verify = verify,
			ExpectedRevision = expectedRevision,
		};

		return await RunAsync(
			progress,
			(session, snapshot, working, token) => DeclarationEditService.ReplaceDocCommentAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.SetAttribute,
		Title = "Add, replace or remove an attribute",
		ReadOnly = false,
		Destructive = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.SetAttribute)]
	public async Task<MemberEditResult> SetAttributeAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DeclarationArgument)] string symbol,
		[Description(ToolDescriptions.AttributeArgument)] string attribute,
		[Description(ToolDescriptions.AttributeActionArgument)] string action = "set",
		[Description(ToolDescriptions.PartialFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.ApplyArgument)] bool apply = true,
		[Description(ToolDescriptions.VerifyArgument)] bool verify = true,
		[Description(ToolDescriptions.VerifyScopeArgument)] string? verifyScope = null,
		[Description(ToolDescriptions.ExpectedRevisionArgument)] long? expectedRevision = null,
		CancellationToken cancellationToken = default)
	{
		var request = new DeclarationEditRequest
		{
			Symbol = symbol,
			Attribute = attribute,
			Action = ActionOf(action),
			FilePath = filePath,
			Apply = apply,
			Verify = verify,
			VerifyScope = ScopeOf(verifyScope),
			ExpectedRevision = expectedRevision,
		};

		return await RunAsync(
			progress,
			(session, snapshot, working, token) => DeclarationEditService.SetAttributeAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token, working),
			cancellationToken);
	}

	/// <summary>
	/// The queue, the progress split and the session, which every declaration edit needs and none of
	/// them varies.
	/// </summary>
	private async Task<MemberEditResult> RunAsync(
		IProgress<ProgressNotificationValue> progress,
		Func<WorkspaceSession, WorkspaceSnapshot, IWorkProgress, CancellationToken, Task<MutationResult<MemberEditResult>>> work,
		CancellationToken cancellationToken)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var session = await host.SessionAsync();

		return await session.MutateAsync(
			(snapshot, token) => work(session, snapshot, working, token), cancellationToken);
	}

	/// <summary>
	/// The action a caller named, or Set where they named nothing. An unrecognised one is refused
	/// rather than taken as Set, which would replace an attribute a caller meant to add.
	/// </summary>
	private static AttributeAction ActionOf(string? requested)
	{
		if (string.IsNullOrWhiteSpace(requested)) return AttributeAction.Set;

		if (Enum.TryParse<AttributeAction>(requested, ignoreCase: true, out var action)) return action;

		throw new ArgumentException($"'{requested}' is not an action. Use set, add, or remove.");
	}

	/// <summary>
	/// Where a caller asked to insert, or null where they asked for none. An unrecognised name is
	/// refused rather than taken as one end: inserting at the wrong end of a body compiles.
	/// </summary>
	private static BodyPosition? PositionOf(string? requested)
	{
		if (string.IsNullOrWhiteSpace(requested)) return null;

		if (Enum.TryParse<BodyPosition>(requested, ignoreCase: true, out var position)) return position;

		throw new ArgumentException($"'{requested}' is not a position. Use start or end.");
	}

	/// <summary>
	/// The scope a caller named, or Auto where they named nothing. A name that is not one of the four
	/// is refused rather than taken as Auto: falling back silently would say the edit was checked
	/// against dependents when it was not, which is the one thing a verification must never do.
	/// </summary>
	private static VerifyScope ScopeOf(string? requested)
	{
		if (string.IsNullOrWhiteSpace(requested)) return VerifyScope.Auto;

		if (Enum.TryParse<VerifyScope>(requested, ignoreCase: true, out var scope)) return scope;

		throw new ArgumentException(
			$"'{requested}' is not a verification scope. Use auto, file, dependents, or solution.");
	}
}
