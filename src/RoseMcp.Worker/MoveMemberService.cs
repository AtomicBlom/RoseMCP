using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Moves a member from one type to another, and takes its call sites with it.
/// <para>
/// One tool rather than an add followed by a delete, for two reasons. It is one solution change
/// set, so a failure halfway leaves nothing rather than a member declared twice. And the call sites
/// are the part a person forgets: the choice between qualifying each one and adding a
/// <c>using static</c> is made once per file, with nothing to remind them what they chose in the
/// last one -- which is how a move produces a diff that is half of each.
/// </para>
/// <para>
/// Static members only. Moving an instance member changes what <c>this</c> means inside it, and
/// every call site needs a receiver that the old code had no reason to have to hand; doing that
/// safely is a different operation and refusing is better than half of it.
/// </para>
/// </summary>
public static class MoveMemberService
{
	/// <summary>How many introduced errors come back before the caller should read the diff instead.</summary>
	private const int Listed = 20;

	public static async Task<MutationResult<MemberEditResult>> MoveAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		MoveMemberRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		snapshot.RefuseIfMoved(request.ExpectedRevision);

		var notices = new List<string>(snapshot.Notices);

		progress?.Report($"Resolving {request.Symbol}", 0);

		var source = await DeclarationLocator.FindMemberAsync(
			snapshot.Solution, request.Symbol, request.FilePath, cancellationToken);

		var target = await DeclarationLocator.FindTypeAsync(
			snapshot.Solution, request.TargetType, filePath: null, cancellationToken);

		Guard(source, target);

		progress?.Report("Finding the call sites", 20);

		var references = await SymbolFinder.FindReferencesAsync(source.Symbol, snapshot.Solution, cancellationToken);

		var sites = references
			.SelectMany(reference => reference.Locations)
			.Where(location => !location.IsImplicit && location.Location.IsInSource)
			.Select(location => location.Location)
			.ToArray();

		progress?.Report("Moving it", 45);

		var moved = await ApplyAsync(snapshot.Solution, source, target, request, sites, notices, cancellationToken);

		progress?.Report(request.Apply ? "Writing the files" : "Building the diff", 70);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, moved, request.Apply, noteSelfWrite, cancellationToken);

		var verification = Verification.NotRun;
		var path = source.Document.FilePath!;

		if (request.Verify && outcome.ChangedFiles.Count > 0)
		{
			progress?.Report("Compiling to see what the move did", 80);

			verification = await EditVerification.RunAsync(
				diagnostics,
				snapshot.Solution,
				moved,
				EditVerification.ScopeFor(moved, path, source.Symbol, request.VerifyScope),
				path,
				cancellationToken);
		}

		notices.AddRange(Notices(request, verification, outcome, sites.Length, target));

		var result = new MemberEditResult
		{
			Revision = snapshot.Revision,
			Symbol = source.Signature,
			FilePath = TargetPath(target),
			Line = 0,
			Members = [source.Symbol.Name],
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			Verified = verification.Ran,
			IntroducedDiagnostics = [.. verification.Introduced.Take(Listed)],
			ResolvedDiagnosticCount = verification.ResolvedCount,
			TotalErrorCount = verification.TotalCount,
			ProjectsChecked = verification.Projects,
			DependentsNotChecked = request.Verify && outcome.ChangedFiles.Count > 0
				? EditVerification.SkippedDependents(moved, path, source.Symbol, request.VerifyScope)
				: [],
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<MemberEditResult>(result, request.Apply && outcome.ChangedFiles.Count > 0 ? moved : null);
	}

	/// <summary>
	/// The two refusals worth making before anything is written: a member whose move would change
	/// what it means, and a move to where it already is.
	/// </summary>
	private static void Guard(DeclarationTarget source, TypeTarget target)
	{
		if (SymbolEqualityComparer.Default.Equals(source.Symbol.ContainingType, target.Symbol))
		{
			throw new ArgumentException($"{source.Signature} is already in {target.Symbol.Name}.");
		}

		if (source.Symbol.IsStatic) return;

		throw new ArgumentException(
			$"{source.Signature} is an instance member, and moving one changes what 'this' means inside it -- "
				+ "every call site would need a receiver it has no reason to have to hand. Make it static "
					+ "first, with rose_replace_member, or move it by hand.");
	}

	/// <summary>
	/// The whole move as one change set: out of the source type, into the target, and every call site
	/// pointed at the new home.
	/// </summary>
	private static async Task<Solution> ApplyAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		MoveMemberRequest request,
		IReadOnlyList<Location> sites,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var qualified = target.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		// Call sites first, while every position still means what it did when they were found. Editing
		// the declarations first would move the offsets the reference search returned.
		var current = request.CallSites == CallSiteStyle.Qualify
			? await QualifyAsync(solution, source, target, sites, cancellationToken)
			: await ImportAsync(solution, sites, qualified, cancellationToken);

		current = await RemoveAsync(current, source, cancellationToken);
		current = await InsertAsync(current, source, target, notices, cancellationToken);

		return current;
	}

	/// <summary>Writes the new type in front of every call.</summary>
	private static async Task<Solution> QualifyAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		IReadOnlyList<Location> sites,
		CancellationToken cancellationToken)
	{
		var current = solution;

		foreach (var group in sites.GroupBy(site => site.SourceTree))
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (group.Key is null || current.GetDocument(group.Key) is not { } document) continue;
			if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) continue;

			var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

			foreach (var site in group)
			{
				var node = root.FindNode(site.SourceSpan, getInnermostNodeForTie: true);
				if (node is not SimpleNameSyntax name) continue;

				// An already-qualified call replaces the whole access, so Source.Member becomes
				// Target.Member rather than Source.Target.Member.
				var replaced = name.Parent is MemberAccessExpressionSyntax access && access.Name == name
					? (SyntaxNode)access
					: name;

				replacements[replaced] = Access(target, source.Symbol.Name)
					.WithTriviaFrom(replaced);
			}

			if (replacements.Count == 0) continue;

			current = current.WithDocumentSyntaxRoot(
				document.Id,
				root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]));
		}

		return current;
	}

	/// <summary>Adds a using static to every file that calls it, leaving the calls alone.</summary>
	private static async Task<Solution> ImportAsync(
		Solution solution,
		IReadOnlyList<Location> sites,
		string qualified,
		CancellationToken cancellationToken)
	{
		var current = solution;
		var name = $"static {qualified.Replace("global::", string.Empty, StringComparison.Ordinal)}";

		foreach (var group in sites.GroupBy(site => site.SourceTree))
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (group.Key is null || current.GetDocument(group.Key) is not { } document) continue;

			current = await ResolvedImports.ApplyAsync(current, document.Id, [name], cancellationToken);
		}

		return current;
	}

	private static async Task<Solution> RemoveAsync(
		Solution solution,
		DeclarationTarget source,
		CancellationToken cancellationToken)
	{
		if (solution.GetDocument(source.Document.Id) is not { } document) return solution;
		if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) return solution;

		var declaration = root.DescendantNodes()
			.OfType<MemberDeclarationSyntax>()
			.FirstOrDefault(node => node.Span == source.Declaration.Span);

		if (declaration?.Parent is not { } parent) return solution;

		var without = parent.RemoveNode(
			declaration,
			SyntaxRemoveOptions.KeepNoTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives);

		return without is null
			? solution
			: solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(parent, without));
	}

	/// <summary>
	/// Puts the declaration into the target type, exactly as it was written -- documentation comment,
	/// attributes and all -- reindented for where it now sits.
	/// </summary>
	private static async Task<Solution> InsertAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		if (solution.GetDocument(target.Document.Id) is not { } document) return solution;
		if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) return solution;

		var declaration = root.DescendantNodes()
			.OfType<BaseTypeDeclarationSyntax>()
			.FirstOrDefault(node => node.Span == target.Declaration.Span);

		if (declaration is not TypeDeclarationSyntax type)
		{
			throw new ArgumentException($"{target.Symbol.Name} has no member list to move into.");
		}

		var text = await document.GetTextAsync(cancellationToken);
		var indent = type.Members.Count > 0
			? IndentAt(text, type.Members[0].SpanStart)
			: IndentAt(text, type.SpanStart) + "\t";

		var moved = MemberSyntax.Parse(
			WithoutDirectives(source.Declaration, notices).ToFullString().Trim(),
			MemberSyntax.KeywordOf(type),
			document.Project.ParseOptions,
			indent,
			Whitespace.Dominant(text));

		if (moved.Count != 1) throw new InvalidOperationException("The member being moved parsed as more than one.");

		var marker = new SyntaxAnnotation();
		var placed = type.WithMembers(type.Members.Add(moved[0].WithAdditionalAnnotations(marker)));

		var written = solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(type, placed));

		notices.Add($"Moved to the end of {target.Symbol.Name}; place it with rose_delete_member and "
			+ "rose_add_member if it belongs somewhere else in the type.");

		return await FormatAsync(written, document.Id, marker, cancellationToken);
	}

	/// <summary>
	/// The declaration with the directives out of its leading trivia, which belong to the file it is
	/// leaving rather than to the member.
	/// <para>
	/// A member that happens to be first inside a region carries the opening <c>#region</c> in its
	/// trivia, and moving that text takes half a pair with it -- CS1038 in the new file and an
	/// unbalanced region in the old one. The documentation comment and the attributes stay, because
	/// those are the member's.
	/// </para>
	/// <para>
	/// Conditional compilation is refused rather than stripped. A <c>#if</c> around a member decides
	/// whether it exists at all, and dropping it would move code into a build it was excluded from --
	/// a change nobody asked for and one nothing downstream would report.
	/// </para>
	/// </summary>
	private static MemberDeclarationSyntax WithoutDirectives(MemberDeclarationSyntax declaration, List<string> notices)
	{
		var leading = declaration.GetLeadingTrivia();

		var directives = leading
			.Select(trivia => trivia.GetStructure())
			.OfType<DirectiveTriviaSyntax>()
			.ToArray();

		if (directives.Length == 0) return declaration;

		var conditional = directives
			.Where(directive => directive is IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax
				or ElseDirectiveTriviaSyntax or EndIfDirectiveTriviaSyntax)
			.ToArray();

		if (conditional.Length > 0)
		{
			throw new ArgumentException(
				$"The member sits under {conditional[0].ToString().Trim()}, which decides whether it is compiled "
					+ "at all. Moving it would put it in a build it was excluded from, so this refuses rather than "
					+ "guess. Take the directive off first, or move it by hand.");
		}

		notices.Add($"Left {directives.Length} directive(s) behind: "
			+ $"{string.Join(", ", directives.Select(directive => directive.ToString().Trim()))} belongs to the "
			+ "source file's structure rather than to the member.");

		return declaration.WithLeadingTrivia(
			leading.Where(trivia => trivia.GetStructure() is not DirectiveTriviaSyntax));
	}

	private static async Task<Solution> FormatAsync(
		Solution solution,
		DocumentId id,
		SyntaxAnnotation marker,
		CancellationToken cancellationToken)
	{
		if (solution.GetDocument(id) is not { } document) return solution;

		var formatted = await Formatter.FormatAsync(document, marker, cancellationToken: cancellationToken);

		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null) return formatted.Project.Solution;

		var rules = Whitespace.RulesFor(formatted.Project, tree, text);
		var nodes = root.GetAnnotatedNodes(marker).ToArray();

		var spans = nodes.Length == 0
			? null
			: (IReadOnlyList<Microsoft.CodeAnalysis.Text.TextSpan>)[.. nodes.Select(node => node.FullSpan)];

		return formatted.WithText(Whitespace.Apply(root, text, rules, spans)).Project.Solution;
	}

	private static ExpressionSyntax Access(TypeTarget target, string member) =>
		SyntaxFactory.ParseExpression($"{target.Symbol.Name}.{member}");

	private static string TargetPath(TypeTarget target) => target.Document.FilePath ?? string.Empty;

	private static string IndentAt(Microsoft.CodeAnalysis.Text.SourceText text, int position)
	{
		var line = text.Lines.GetLineFromPosition(position).ToString();

		return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
	}

	private static IEnumerable<string> Notices(
		MoveMemberRequest request,
		Verification verification,
		WriteOutcome outcome,
		int sites,
		TypeTarget target)
	{
		if (!request.Apply) yield return "Preview only; nothing was written to disk.";

		foreach (var notice in outcome.Notices) yield return notice;

		yield return request.CallSites == CallSiteStyle.Qualify
			? $"{sites} call site(s) now name {target.Symbol.Name}."
			: $"{sites} call site(s) left as written; the files calling it import {target.Symbol.Name} statically.";

		if (!verification.Ran) yield break;

		foreach (var notice in verification.Notices) yield return notice;

		var compiled = string.Join(", ", verification.Projects);

		yield return verification.Introduced.Count == 0
			? $"{compiled} compiles clean."
			: $"{verification.Introduced.Count} error(s) introduced in {compiled}.";
	}
}
