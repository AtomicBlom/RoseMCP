using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Changes what surrounds a declaration -- its documentation comment, or one of its attributes --
/// without rewriting the declaration.
/// <para>
/// This is the half of an edit that has no tool of its own anywhere else, and its absence is what
/// two recorded sessions on this repository blamed for making <em>zero</em> semantic calls. This
/// repository writes fifteen-line documentation comments as a matter of method and the
/// <c>Description</c> text on a tool is half of any tool change, so composing a whole member to
/// change a sentence is a trade nobody takes. The editor wins, and then it takes the code half too.
/// </para>
/// <para>
/// The order is the same as everywhere else that writes: resolve the declaration and validate the
/// payload <em>before</em> the file is opened, so a refusal costs nothing and can never leave a
/// file half-written.
/// </para>
/// </summary>
public static class DeclarationEditService
{
	/// <summary>How many introduced errors come back before the caller should read the diff instead.</summary>
	public static Task<MutationResult<MemberEditResult>> ReplaceDocCommentAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		DeclarationEditRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		DocComment.Guard(request.Comment ?? string.Empty);

		return EditAsync(
			snapshot,
			diagnostics,
			request,
			(declaration, context, notices) =>
			{
				if (!DocComment.Present(declaration.GetLeadingTrivia()))
				{
					notices.Add("The declaration had no documentation comment, so this added one.");
				}

				return declaration.WithLeadingTrivia(
					DocComment.Replace(
						declaration.GetLeadingTrivia(), request.Comment!, context.Indent, context.LineEnding));
			},
			CommentRegion,
			noteSelfWrite,
			cancellationToken,
			progress);
	}

	public static Task<MutationResult<MemberEditResult>> SetAttributeAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		DeclarationEditRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null) =>
		EditAsync(
			snapshot,
			diagnostics,
			request,
			(declaration, context, notices) => request.Parameter is { Length: > 0 } parameter
				? OnParameter(declaration, parameter, request, context, notices)
				: AttributeEdit.Apply(
					declaration,
					request.Attribute ?? string.Empty,
					request.Action,
					context.ParseOptions,
					context.Indent,
					context.LineEnding,
					notices),
			declaration => AttributeRegion(declaration, request.Parameter),
			noteSelfWrite,
			cancellationToken,
			progress);

	/// <summary>
	/// The declaration with the attribute written on one of its parameters.
	/// <para>
	/// The parameter is found by name and a name that matches none is refused with the ones that are
	/// there, because the alternative is writing the attribute nowhere and reporting success.
	/// </para>
	/// </summary>
	private static MemberDeclarationSyntax OnParameter(
		MemberDeclarationSyntax declaration,
		string name,
		DeclarationEditRequest request,
		Context context,
		List<string> notices)
	{
		if (ParameterLists.Of(declaration) is not { } list)
		{
			throw new ArgumentException(
				"This declaration has no parameter list, so it has no parameter to put an attribute on.");
		}

		var matching = list.Parameters.Where(candidate => candidate.Identifier.Text == name).ToArray();

		if (matching.Length == 0)
		{
			var present = list.Parameters.Select(parameter => parameter.Identifier.Text).ToArray();

			throw new ArgumentException(
				$"There is no parameter called {name}. This one takes "
					+ (present.Length == 0 ? "none." : $"{string.Join(", ", present)}."));
		}

		var written = AttributeEdit.Apply(
			matching[0],
			request.Attribute ?? string.Empty,
			request.Action,
			context.ParseOptions,
			notices);

		return declaration.ReplaceNode(matching[0], written);
	}

	/// <summary>
	/// Resolve, rewrite, format, write, compile. One path for both edits, because everything except
	/// the rewrite itself is the same and a second copy is a second place for the ordering to drift.
	/// </summary>
	private static async Task<MutationResult<MemberEditResult>> EditAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		DeclarationEditRequest request,
		Func<MemberDeclarationSyntax, Context, List<string>, MemberDeclarationSyntax> rewrite,
		Func<MemberDeclarationSyntax, TextSpan> asked,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress)
	{
		var edit = EditPipeline.Begin(
			snapshot, diagnostics, request.ExpectedRevision, request.Apply, request.Verify, noteSelfWrite);

		var notices = edit.Notices;

		progress?.Report($"Locating {request.Symbol}", 0);

		var target = await DeclarationLocator.FindMemberAsync(
			snapshot.Solution, request.Symbol, request.FilePath, cancellationToken);

		var document = target.Document;
		var text = await document.GetTextAsync(cancellationToken);

		var context = new Context(
			IndentAt(text, target.Declaration.SpanStart),
			Whitespace.Dominant(text),
			document.Project.ParseOptions);

		progress?.Report("Rewriting the declaration", 30);

		var marker = new SyntaxAnnotation();
		var rewritten = rewrite(target.Declaration, context, notices).WithAdditionalAnnotations(marker);

		var root = await document.GetSyntaxRootAsync(cancellationToken)
			?? throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");

		var edited = document.Project.Solution.WithDocumentSyntaxRoot(
			document.Id, root.ReplaceNode(target.Declaration, rewritten));

		var finished = await FinishAsync(edited, document.Id, marker, cancellationToken);

		progress?.Report(request.Apply ? "Writing the file" : "Building the diff", 60);

		await edit.WriteAsync(finished, Asked.Nothing.And(document, asked(target.Declaration)), cancellationToken);

		var path = document.FilePath!;

		// An attribute reaches outside its own project -- Obsolete as an error is the plain case --
		// so it takes the scope rule. A documentation comment cannot, and its own project's CS1572
		// and CS1573 are the only things it can break.
		var reaches = request.Attribute is not null ? target.Symbol : null;

		if (request.Verify && edit.Changed) progress?.Report("Compiling to see what the edit did", 75);

		await edit.VerifyAsync(
			path, EditVerification.ScopeFor(finished, path, reaches, request.VerifyScope), cancellationToken);

		notices.AddRange(edit.Report());

		var result = new MemberEditResult
		{
			Revision = snapshot.Revision,
			Symbol = target.Signature,
			FilePath = path,
			Line = LineOf(target),
			Members = [target.Declaration is BaseTypeDeclarationSyntax type ? type.Identifier.Text : target.Signature],
			Applied = edit.Applied,
			Diff = edit.Outcome.Diff,
			Verified = edit.Verification.Ran,
			IntroducedDiagnostics = edit.Introduced,
			ResolvedDiagnosticCount = edit.Verification.ResolvedCount,
			TotalErrorCount = edit.Verification.TotalCount,
			ProjectsChecked = edit.Verification.Projects,
			DependentsNotChecked = request.Verify && edit.Changed
				? EditVerification.SkippedDependents(finished, path, reaches, request.VerifyScope)
				: [],
			ChangedFiles = edit.Outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<MemberEditResult>(result, edit.Kept);
	}

	/// <summary>
	/// The two formatting passes over the span that was written, so a repository whose endings are
	/// already inconsistent does not get every line rewritten by a one-declaration change.
	/// </summary>
	private static async Task<Solution> FinishAsync(
		Solution edited,
		DocumentId id,
		SyntaxAnnotation marker,
		CancellationToken cancellationToken)
	{
		var document = edited.GetDocument(id)
			?? throw new InvalidOperationException("The document being written left the solution mid-edit.");

		var formatted = await Formatter.FormatAsync(document, marker, cancellationToken: cancellationToken);

		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");
		}

		var nodes = root.GetAnnotatedNodes(marker).ToArray();
		var rules = Whitespace.RulesFor(formatted.Project, tree, text);

		var spans = nodes.Length == 0
			? null
			: (IReadOnlyList<TextSpan>)[.. nodes.Select(node => node.FullSpan)];

		return formatted.WithText(Whitespace.Apply(root, text, rules, spans)).Project.Solution;
	}

	/// <summary>
	/// What writing a documentation comment asks to change: the trivia in front of the declaration,
	/// where the comment is or where it goes.
	/// </summary>
	private static TextSpan CommentRegion(MemberDeclarationSyntax declaration) =>
		TextSpan.FromBounds(declaration.FullSpan.Start, declaration.SpanStart);

	/// <summary>
	/// What setting an attribute asks to change: the parameter it goes on, or else the declaration's own
	/// attribute lists, or the place in front of its first token where the first one goes.
	/// </summary>
	private static TextSpan AttributeRegion(MemberDeclarationSyntax declaration, string? parameter)
	{
		var named = parameter is { Length: > 0 }
			? ParameterLists.Of(declaration)?.Parameters.FirstOrDefault(candidate => candidate.Identifier.Text == parameter)
			: null;

		if (named is not null) return named.Span;

		var lists = declaration.AttributeLists;
		var after = lists.Count > 0 ? lists[^1].GetLastToken().GetNextToken() : declaration.GetFirstToken();

		return TextSpan.FromBounds(lists.Count > 0 ? lists[0].SpanStart : after.SpanStart, after.SpanStart);
	}

	private static int LineOf(DeclarationTarget target) =>
		target.Declaration.SyntaxTree.GetLineSpan(target.Declaration.Span).StartLinePosition.Line + 1;

	private static string IndentAt(SourceText text, int position)
	{
		var line = text.Lines.GetLineFromPosition(position).ToString();

		return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
	}

	/// <summary>What the file decides about how a written line should look.</summary>
	private readonly record struct Context(string Indent, string LineEnding, ParseOptions? ParseOptions);
}
