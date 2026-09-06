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
	private const int Listed = 20;

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
			(declaration, context, notices) => AttributeEdit.Apply(
				declaration,
				request.Attribute ?? string.Empty,
				request.Action,
				context.ParseOptions,
				context.Indent,
				context.LineEnding,
				notices),
			noteSelfWrite,
			cancellationToken,
			progress);

	/// <summary>
	/// Resolve, rewrite, format, write, compile. One path for both edits, because everything except
	/// the rewrite itself is the same and a second copy is a second place for the ordering to drift.
	/// </summary>
	private static async Task<MutationResult<MemberEditResult>> EditAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		DeclarationEditRequest request,
		Func<MemberDeclarationSyntax, Context, List<string>, MemberDeclarationSyntax> rewrite,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress)
	{
		snapshot.RefuseIfMoved(request.ExpectedRevision);

		var notices = new List<string>(snapshot.Notices);

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

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, finished, request.Apply, noteSelfWrite, cancellationToken);

		if (outcome.ChangedFiles.Count == 0) notices.Add("The declaration already said exactly that, so nothing changed.");

		var verification = Verification.NotRun;
		var path = document.FilePath!;

		// An attribute reaches outside its own project -- Obsolete as an error is the plain case --
		// so it takes the scope rule. A documentation comment cannot, and its own project's CS1572
		// and CS1573 are the only things it can break.
		var reaches = request.Attribute is not null ? target.Symbol : null;

		if (request.Verify && outcome.ChangedFiles.Count > 0)
		{
			progress?.Report("Compiling to see what the edit did", 75);

			verification = await EditVerification.RunAsync(
				diagnostics,
				snapshot.Solution,
				finished,
				EditVerification.ScopeFor(finished, path, reaches, request.VerifyScope),
				path,
				cancellationToken);
		}

		notices.AddRange(Notices(request, verification, outcome));

		var result = new MemberEditResult
		{
			Revision = snapshot.Revision,
			Symbol = target.Signature,
			FilePath = path,
			Line = LineOf(target),
			Members = [target.Declaration is BaseTypeDeclarationSyntax type ? type.Identifier.Text : target.Signature],
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			Verified = verification.Ran,
			IntroducedDiagnostics = [.. verification.Introduced.Take(Listed)],
			ResolvedDiagnosticCount = verification.ResolvedCount,
			TotalErrorCount = verification.TotalCount,
			ProjectsChecked = verification.Projects,
			DependentsNotChecked = request.Verify && outcome.ChangedFiles.Count > 0
				? EditVerification.SkippedDependents(finished, path, reaches, request.VerifyScope)
				: [],
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		var changed = request.Apply && outcome.ChangedFiles.Count > 0 ? finished : null;

		return new MutationResult<MemberEditResult>(result, changed);
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

	private static IEnumerable<string> Notices(
		DeclarationEditRequest request,
		Verification verification,
		WriteOutcome outcome)
	{
		if (!request.Apply) yield return "Preview only; nothing was written to disk.";

		foreach (var notice in outcome.Notices) yield return notice;

		if (!verification.Ran)
		{
			if (outcome.ChangedFiles.Count > 0)
			{
				yield return "Nothing was compiled, so this says nothing about whether the code is sound. Pass "
					+ "verify=true, or ask rose_diagnostics.";
			}

			yield break;
		}

		foreach (var notice in verification.Notices) yield return notice;

		var compiled = string.Join(", ", verification.Projects);

		yield return verification.Introduced.Count == 0
			? $"{compiled} compiles clean."
			: $"{verification.Introduced.Count} error(s) introduced in {compiled}.";

		foreach (var suggestion in verification.Suggestions) yield return suggestion;
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
