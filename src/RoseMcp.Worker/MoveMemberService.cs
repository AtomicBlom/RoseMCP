using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

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
/// An instance member moves only where nothing refers to it and nothing in it reads the instance it
/// leaves that the type it goes to cannot answer -- a test method moving between fixtures. Anywhere
/// else, moving one changes what <c>this</c> means inside it and every call site needs a receiver the
/// old code had no reason to have to hand; doing that safely is a different operation, and refusing
/// is better than half of it. <see cref="InstanceMove"/> decides which.
/// </para>
/// </summary>
public static class MoveMemberService
{
	/// <summary>How many introduced errors come back before the caller should read the diff instead.</summary>
	public static async Task<MutationResult<MemberEditResult>> MoveAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		MoveMemberRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var edit = EditPipeline.Begin(
			snapshot, diagnostics, request.ExpectedRevision, request.Apply, request.Verify, noteSelfWrite);

		var notices = edit.Notices;

		progress?.Report($"Resolving {request.Symbol}", 0);

		var source = await DeclarationLocator.FindMemberAsync(
			snapshot.Solution, request.Symbol, request.FilePath, cancellationToken);

		var target = await DeclarationLocator.FindTypeAsync(
			snapshot.Solution, request.TargetType, filePath: null, cancellationToken);

		Guard(source, target);

		progress?.Report("Finding the call sites", 20);

		var references = await SymbolFinder.FindReferencesAsync(source.Symbol, snapshot.Solution, cancellationToken);

		IReadOnlyList<Location> sites =
		[
			.. references
				.SelectMany(reference => reference.Locations)
				.Where(location => !location.IsImplicit && location.Location.IsInSource)
				.Select(location => location.Location),
		];

		var instance = !source.Symbol.IsStatic;

		if (instance)
		{
			sites = InstanceMove.Outside(sites, source);
			await InstanceMove.GuardAsync(snapshot.Solution, source, target, sites, cancellationToken);
		}

		progress?.Report("Moving it", 45);

		var (moved, asked) = await ApplyAsync(snapshot.Solution, source, target, request, sites, notices, cancellationToken);

		if (instance) await InstanceMove.ConfirmAsync(snapshot.Solution, moved, source, target, cancellationToken);

		progress?.Report(request.Apply ? "Writing the files" : "Building the diff", 70);

		await edit.WriteAsync(moved, asked, cancellationToken);

		var path = source.Document.FilePath!;

		if (request.Verify && edit.Changed) progress?.Report("Compiling to see what the move did", 80);

		await edit.VerifyAsync(
			path, EditVerification.ScopeFor(moved, path, source.Symbol, request.VerifyScope), [], cancellationToken);

		notices.AddRange(Notices(request, sites.Count, instance, target));
		notices.AddRange(edit.Report());

		var result = new MemberEditResult
		{
			Revision = snapshot.Revision,
			Symbol = source.Signature,
			FilePath = TargetPath(target),
			Line = 0,
			Members = [source.Symbol.Name],
			Applied = edit.Applied,
			Diff = edit.Outcome.Diff,
			Verified = edit.Verification.Ran,
			IntroducedDiagnostics = edit.Introduced,
			ResolvedDiagnosticCount = edit.Verification.ResolvedCount,
			TotalErrorCount = edit.Verification.TotalCount,
			ProjectsChecked = edit.Verification.Projects,
			DependentsNotChecked = request.Verify && edit.Changed
				? EditVerification.SkippedDependents(moved, path, source.Symbol, request.VerifyScope)
				: [],
			ChangedFiles = edit.Outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<MemberEditResult>(result, edit.Kept);
	}

	/// <summary>
	/// The refusals worth making before anything is looked up: a move to where the member already is,
	/// and an instance member of a kind, or going to a type, that cannot take part in one.
	/// </summary>
	private static void Guard(DeclarationTarget source, TypeTarget target)
	{
		if (SymbolEqualityComparer.Default.Equals(source.Symbol.ContainingType, target.Symbol))
		{
			throw new ArgumentException($"{source.Signature} is already in {target.Symbol.Name}.");
		}

		if (source.Symbol.IsStatic) return;

		InstanceMove.GuardShape(source, target);
	}

	/// <summary>
	/// The whole move as one change set: out of the source type, into the target, and every call site
	/// pointed at the new home -- with what each of those asks to change, which is the member where it
	/// was, the place it goes, and the call sites, and nothing else in either type.
	/// </summary>
	private static async Task<(Solution Solution, Asked Asked)> ApplyAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		MoveMemberRequest request,
		IReadOnlyList<Location> sites,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var qualified = target.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		// By document rather than by tree: marking gives the files it touches new trees, and a call site
		// found in the old one would no longer find its document through it.
		var calls = sites
			.Select(site => (Id: solution.GetDocumentId(site.SourceTree), Span: site.SourceSpan))
			.Where(call => call.Id is not null)
			.GroupBy(call => call.Id!, call => call.Span)
			.ToArray();

		var (marked, moving, into) = await MarkAsync(solution, source, target, cancellationToken);

		// Call sites first, while every position still means what it did when they were found. Editing
		// the declarations first would move the offsets the reference search returned.
		var (current, asked) = request.CallSites == CallSiteStyle.Qualify
			? await QualifyAsync(marked, source, target, calls, cancellationToken)
			: await ImportAsync(marked, calls, qualified, cancellationToken);

		current = await RemoveAsync(current, source, moving, cancellationToken);
		current = await InsertAsync(current, source, target, into, notices, cancellationToken);

		asked = asked
			.And(source.Document, source.Declaration.FullSpan)
			.And(target.Document, new TextSpan(InsertionPoint(target), 0));

		return (current, asked);
	}

	/// <summary>
	/// The solution with the declaration being moved and the type it goes into each carrying an
	/// annotation, so both are found again once the call sites are rewritten.
	/// <para>
	/// Not by span. Qualifying a call above either one moves every position under it, so a node looked
	/// for where it stood is not there -- and for the removal that leaves the member declared in both
	/// types, which compiles, because the types differ, so nothing downstream says so.
	/// </para>
	/// </summary>
	private static async Task<(Solution Solution, SyntaxAnnotation Moving, SyntaxAnnotation Into)> MarkAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		CancellationToken cancellationToken)
	{
		var moving = new SyntaxAnnotation();
		var into = new SyntaxAnnotation();

		var marked = await AnnotateAsync(
			solution, source.Document.Id, source.Declaration, moving, $"the declaration of {source.Signature}", cancellationToken);
		marked = await AnnotateAsync(
			marked, target.Document.Id, target.Declaration, into, $"the declaration of {target.Symbol.Name}", cancellationToken);

		return (marked, moving, into);
	}

	/// <summary>
	/// One node annotated in place. Found by span and kind rather than by identity, because when the
	/// member and its new home share a file the first annotation has already replaced that file's root;
	/// an annotation adds no text, so the span still holds.
	/// </summary>
	private static async Task<Solution> AnnotateAsync(
		Solution solution,
		DocumentId id,
		SyntaxNode original,
		SyntaxAnnotation annotation,
		string what,
		CancellationToken cancellationToken)
	{
		var root = solution.GetDocument(id) is { } document
			? await document.GetSyntaxRootAsync(cancellationToken)
			: null;

		var node = root?.DescendantNodesAndSelf()
			.FirstOrDefault(candidate => candidate.Span == original.Span && candidate.RawKind == original.RawKind);

		if (root is null || node is null) throw Lost(what);

		return solution.WithDocumentSyntaxRoot(id, root.ReplaceNode(node, node.WithAdditionalAnnotations(annotation)));
	}

	/// <summary>
	/// A node the move could not find in the solution it was editing. Never the caller's error, and
	/// never survivable: a move that cannot take the member out of its old type has not happened, and
	/// writing the rest of it leaves the member declared twice in code that compiles.
	/// </summary>
	private static InvalidOperationException Lost(string what) =>
		new($"The move lost track of {what} while it rewrote the files, so nothing was written.");

	/// <summary>Writes the new type in front of every call, asking for each name it replaces.</summary>
	private static async Task<(Solution Solution, Asked Asked)> QualifyAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		IReadOnlyList<IGrouping<DocumentId, TextSpan>> calls,
		CancellationToken cancellationToken)
	{
		var current = solution;
		var asked = Asked.Nothing;

		foreach (var group in calls)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (current.GetDocument(group.Key) is not { } document) continue;
			if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) continue;

			var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

			foreach (var span in group)
			{
				var node = root.FindNode(span, getInnermostNodeForTie: true);
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

			asked = asked.And(document, replacements.Keys.Select(replaced => replaced.Span));

			current = current.WithDocumentSyntaxRoot(
				document.Id,
				root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]));
		}

		return (current, asked);
	}

	/// <summary>
	/// Adds a using static to every file that calls it, leaving the calls alone, and asking for the
	/// imports of each of those files.
	/// </summary>
	private static async Task<(Solution Solution, Asked Asked)> ImportAsync(
		Solution solution,
		IReadOnlyList<IGrouping<DocumentId, TextSpan>> calls,
		string qualified,
		CancellationToken cancellationToken)
	{
		var current = solution;
		var asked = Asked.Nothing;
		var name = $"static {qualified.Replace("global::", string.Empty, StringComparison.Ordinal)}";

		foreach (var group in calls)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (current.GetDocument(group.Key) is not { } document) continue;

			asked = asked.And(document, await EditImports.RegionAsync(document, cancellationToken));
			var rules = await Whitespace.RulesForAsync(document, cancellationToken);
			current = await ResolvedImports.ApplyAsync(current, document.Id, [name], rules, cancellationToken);
		}

		return (current, asked);
	}

	/// <summary>
	/// Takes the declaration out of the type it is leaving, found by the annotation it was marked with
	/// before the call sites were rewritten. Not finding it is an error rather than a solution handed
	/// back unchanged, since a move that leaves its source in place has not happened.
	/// </summary>
	private static async Task<Solution> RemoveAsync(
		Solution solution,
		DeclarationTarget source,
		SyntaxAnnotation moving,
		CancellationToken cancellationToken)
	{
		var root = solution.GetDocument(source.Document.Id) is { } document
			? await document.GetSyntaxRootAsync(cancellationToken)
			: null;

		var declaration = root?.GetAnnotatedNodes(moving).OfType<MemberDeclarationSyntax>().SingleOrDefault();

		if (root is null || declaration?.Parent is not { } parent) throw Lost($"the declaration of {source.Signature}");

		var without = parent.RemoveNode(
			declaration,
			SyntaxRemoveOptions.KeepNoTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)
			?? throw Lost($"the type {source.Signature} is declared in");

		return solution.WithDocumentSyntaxRoot(source.Document.Id, root.ReplaceNode(parent, without));
	}

	/// <summary>
	/// Puts the declaration into the target type, exactly as it was written -- documentation comment,
	/// attributes and all -- reindented for where it now sits. The type is found by the annotation it
	/// was marked with before the call sites were rewritten.
	/// </summary>
	private static async Task<Solution> InsertAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		SyntaxAnnotation into,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var document = solution.GetDocument(target.Document.Id);
		var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);

		var declaration = root?.GetAnnotatedNodes(into).OfType<BaseTypeDeclarationSyntax>().SingleOrDefault();

		if (document is null || root is null || declaration is null) throw Lost($"the declaration of {target.Symbol.Name}");

		if (declaration is not TypeDeclarationSyntax type)
		{
			throw new ArgumentException($"{target.Symbol.Name} has no member list to move into.");
		}

		var text = await document.GetTextAsync(cancellationToken);
		var rules = await Whitespace.RulesForAsync(target.Document, cancellationToken);
		var indent = type.Members.Count > 0
			? Whitespace.IndentAt(text, type.Members[0].SpanStart)
			: Whitespace.IndentAt(text, type.SpanStart) + rules.IndentUnit;

		// The line breaks above the declaration come off; the indentation of its first line does not.
		// That indentation is the baseline every wrapped line is measured against, and taking it away
		// leaves the continuations carrying the old type's level with the new type's added on top of it
		// -- a signature that arrives wrapped two levels in lands four, and nothing downstream says so.
		var moved = MemberSyntax.Parse(
			WithoutDirectives(source.Declaration, notices).ToFullString().TrimStart('\r', '\n').TrimEnd(),
			MemberSyntax.KeywordOf(type),
			document.Project.ParseOptions,
			indent,
			rules.LineEnding,
			literals => notices.Add(MemberSyntax.RewrittenMemberEndings(literals, LineEndings.Name(rules.LineEnding))),
			count => notices.Add(MemberSyntax.ReindentedLiteral(count)));

		if (moved.Count != 1) throw new InvalidOperationException("The member being moved parsed as more than one.");

		var marker = new SyntaxAnnotation();

		// Through the same preparation the add path uses. Handing the formatter a member with no
		// leading whitespace has it recompute the indentation, on top of the shift that already
		// applied it, and every wrapped line lands a level too deep.
		var placed = type.WithMembers(type.Members.Add(MemberSyntax.Prepared(
			moved[0],
			blankBefore: type.Members.Count > 0,
			blankAfter: false,
			rules.LineEnding,
			indent,
			marker)));

		var written = solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(type, placed));

		notices.Add($"Moved to the end of {target.Symbol.Name}; place it with rose_delete_member and "
			+ "rose_add_member if it belongs somewhere else in the type.");

		return await FormatAsync(written, document.Id, marker, rules, cancellationToken);
	}

	/// <summary>
	/// Where <see cref="InsertAsync"/> puts the member in the type as it was: after its last member, or
	/// inside the braces of a type that has none.
	/// </summary>
	private static int InsertionPoint(TypeTarget target) => target.Declaration switch
	{
		TypeDeclarationSyntax { Members.Count: > 0 } type => type.Members[^1].FullSpan.End,
		TypeDeclarationSyntax type => type.OpenBraceToken.FullSpan.End,
		var other => other.Span.End,
	};

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
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		if (solution.GetDocument(id) is not { } document) return solution;

		var options = await Whitespace.FormattingOptionsAsync(document, rules, cancellationToken);
		var formatted = await Formatter.FormatAsync(document, marker, options, cancellationToken);

		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null) return formatted.Project.Solution;

		var nodes = root.GetAnnotatedNodes(marker).ToArray();

		var spans = nodes.Length == 0
			? null
			: (IReadOnlyList<Microsoft.CodeAnalysis.Text.TextSpan>)[.. nodes.Select(node => node.FullSpan)];

		return formatted.WithText(Whitespace.Apply(root, text, rules, spans)).Project.Solution;
	}

	private static ExpressionSyntax Access(TypeTarget target, string member) =>
		SyntaxFactory.ParseExpression($"{target.Symbol.Name}.{member}");

	private static string TargetPath(TypeTarget target) => target.Document.FilePath ?? string.Empty;

	/// <summary>
	/// What a move has to say that no other writing tool does. Everything about the write and the
	/// compile comes from <see cref="EditPipeline.Report"/>, which runs after this.
	/// </summary>
	private static IEnumerable<string> Notices(MoveMemberRequest request, int sites, bool instance, TypeTarget target)
	{
		if (instance)
		{
			yield return $"Moved as an instance member: nothing refers to it, so no call site changed, and no name in "
				+ $"it means something else in {target.Symbol.Name}.";

			yield break;
		}

		yield return request.CallSites == CallSiteStyle.Qualify
			? $"{sites} call site(s) now name {target.Symbol.Name}."
			: $"{sites} call site(s) left as written; the files calling it import {target.Symbol.Name} statically.";
	}
}
