using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Changes a member's parameters, and everything that has to change with them.
/// <para>
/// Renaming exists here because find-and-replace gets a rename wrong. Changing a signature is the
/// same capability and the same argument, over a harder search: a single optional parameter can
/// cross six layers of a forwarding chain, and a call site found by grep and edited by text anchor
/// is one that has to be found at all -- a missed one surfaces as CS7036 from a build, if the
/// parameter is required, and not at all if it is not.
/// </para>
/// <para>
/// What makes it more than a convenience is the two things a person doing it by hand forgets. The
/// first is the declarations that have to move together -- the base declaration, and every override
/// and implementation of it -- because changing only the one named does not compile. The second is
/// the call sites that <em>do</em> compile: a new parameter with a default breaks nothing, so a
/// forwarder that goes on passing the default is a silent bug, and that is exactly the shape of the
/// failure this was built from. Those are reported rather than guessed at.
/// </para>
/// </summary>
public static class ChangeSignatureService
{
	/// <summary>How many introduced errors come back before the caller should be reading the diff instead.</summary>
	public static async Task<MutationResult<SignatureChangeResult>> ChangeAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		ChangeSignatureRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var edit = EditPipeline.Begin(
			snapshot, diagnostics, request.ExpectedRevision, request.Apply, request.Verify, noteSelfWrite);

		var notices = edit.Notices;

		progress?.Report($"Resolving {request.Symbol}", 0);

		var target = await DeclarationLocator.FindSymbolAsync(
			snapshot.Solution, request.Symbol, request.FilePath, cancellationToken);

		var accessibility = request.Accessibility is { } written ? AccessibilityModifiers.Parse(written) : (Accessibility?)null;

		if (request.Parameters is null && accessibility is null)
		{
			throw new ArgumentException(
				"Nothing to change. Pass parameters for the parameter list, accessibility for who may see it, or both.");
		}

		var plan = ParameterPlan.None;
		var wanted = default(SeparatedSyntaxList<ParameterSyntax>);
		IReadOnlyList<IMethodSymbol> group = [];

		if (request.Parameters is { } requested)
		{
			if (target.Symbol is not IMethodSymbol method || ParameterLists.Of(target.Declaration) is not { } parameters)
			{
				throw new ArgumentException(
					$"{target.Signature} has no parameter list to change. Parameters belong to a method, a constructor "
						+ "or an operator; accessibility on its own changes anything else, and rose_replace_member "
						+ "writes any other declaration whole.");
			}

			var text = await target.Document.GetTextAsync(cancellationToken);
			var indent = Whitespace.IndentAt(text, target.Declaration.SpanStart);

			wanted = MemberSyntax.ParseParameters(
				requested,
				target.Document.Project.ParseOptions,
				indent,
				(await Whitespace.RulesForAsync(target.Document, cancellationToken)).IndentUnit);
			plan = ParameterPlan.For(parameters.Parameters, wanted);

			if (plan.WhyImpossible() is { } refusal) throw new ArgumentException(refusal);

			progress?.Report("Finding the declarations that move with it", 10);

			group = await GroupAsync(snapshot.Solution, method, cancellationToken);

			if (Clash(group, plan, cancellationToken) is { } clash) throw new ArgumentException(clash);
		}

		var supplied = Supplied(request.Arguments);

		var access = accessibility is { } wantedAccess
			? await AccessGroupAsync(snapshot.Solution, target, wantedAccess, cancellationToken)
			: [];

		progress?.Report(request.Parameters is null ? "Finding the declarations" : "Finding the call sites", 25);

		var work = await GatherAsync(
			snapshot.Solution, group, access, target.Symbol, plan, wanted, notices, cancellationToken);

		// After the call sites and before anything is written, so a refusal still costs nothing and
		// can be true of the sites it names. Asked of the plan alone it fired on a member nothing
		// calls and on calls written ahead of the parameter they pass, saying both had nothing to
		// pass.
		GuardMissingArguments(
			plan,
			supplied,
			await BindingCallSiteCountAsync(snapshot.Solution, work, cancellationToken));

		progress?.Report("Rewriting", 55);

		var applied = await ApplyAsync(snapshot.Solution, work, plan, supplied, cancellationToken);

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 70);

		await edit.WriteAsync(applied.Solution, AskedOf(snapshot.Solution, work), cancellationToken);

		if (request.Verify && edit.Changed) progress?.Report("Compiling the solution to see what moved", 80);

		await edit.VerifyAsync(
			target.FilePath, EditVerification.AllProjects(applied.Solution), cancellationToken);

		var unchanged = await DescribeUnchangedAsync(snapshot.Solution, work, applied, plan, cancellationToken);

		notices.AddRange(Notices(request, plan, applied, edit.Verification, edit.Outcome, unchanged, target.Symbol, access));

		var result = new SignatureChangeResult
		{
			Revision = snapshot.Revision,
			Symbol = target.Signature,
			Parameters = request.Parameters?.Trim(),
			Accessibility = accessibility is { } given ? AccessibilityModifiers.Spelled(given) : null,
			Applied = edit.Applied,
			Diff = edit.Outcome.Diff,
			UpdatedDeclarations = await DescribeAsync(snapshot.Solution, work.SelectMany(w => w.DeclarationSites), cancellationToken),
			UpdatedCallSites = await DescribeAsync(snapshot.Solution, applied.RewrittenCallSites, cancellationToken),
			UnchangedCallSites = unchanged,
			DocumentationUpdated = applied.Documentation,
			Verified = edit.Verification.Ran,
			IntroducedDiagnostics = edit.Introduced,
			ResolvedDiagnosticCount = edit.Verification.ResolvedCount,
			TotalErrorCount = edit.Verification.TotalCount,
			ProjectsChecked = edit.Verification.Projects,
			ChangedFiles = edit.Outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<SignatureChangeResult>(result, edit.Kept);
	}

	/// <summary>
	/// The declarations that have to change together: the member, the declaration it overrides or
	/// implements all the way up, and everything else that overrides or implements any of those.
	/// <para>
	/// Not optional. A virtual method whose override keeps the old parameters does not compile, and
	/// an interface member whose implementations keep theirs does not either -- so a tool that
	/// changed only what it was pointed at would break the build every time the member was virtual.
	/// </para>
	/// <para>
	/// Closed transitively rather than one level deep, because the two relations chain. Starting at an
	/// interface member finds the class that implements it; the override <em>of that class</em> is a
	/// second step, and stopping after the first leaves it with the old parameters and CS0115 -- a
	/// build broken by the tool whose whole point is not breaking it.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<IMethodSymbol>> GroupAsync(
		Solution solution,
		IMethodSymbol method,
		CancellationToken cancellationToken)
	{
		var group = new List<IMethodSymbol>();
		var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		var pending = new Queue<IMethodSymbol>();

		void Consider(IMethodSymbol candidate)
		{
			if (!seen.Add(candidate)) return;

			group.Add(candidate);
			pending.Enqueue(candidate);
		}

		// Upwards first, so the roots are in the group before anything is asked what derives from them.
		for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
		{
			Consider(current);

			foreach (var declared in InterfaceMembers(current)) Consider(declared);
		}

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var next = pending.Dequeue();

			foreach (var over in await SymbolFinder.FindOverridesAsync(next, solution, cancellationToken: cancellationToken))
			{
				if (over is IMethodSymbol found) Consider(found);
			}

			foreach (var implementation in await SymbolFinder.FindImplementationsAsync(next, solution, cancellationToken: cancellationToken))
			{
				if (implementation is IMethodSymbol found) Consider(found);
			}
		}

		return group;
	}

	/// <summary>
	/// Why a new parameter's name cannot go into the bodies that would receive it, or null where it
	/// can.
	/// <para>
	/// A local, a lambda's parameter or a pattern variable in the body with the new name is CS0136 the
	/// moment the parameter lands. That is knowable before anything is written, and a caller told first
	/// picks another name rather than repairing one -- the compile afterwards would report the error,
	/// but in a file this has already rewritten across every override and call site.
	/// </para>
	/// </summary>
	private static string? Clash(
		IReadOnlyList<IMethodSymbol> group,
		ParameterPlan plan,
		CancellationToken cancellationToken)
	{
		var added = plan.Added.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

		if (added.Count == 0) return null;

		foreach (var member in group)
		{
			foreach (var reference in member.DeclaringSyntaxReferences)
			{
				if (reference.GetSyntax(cancellationToken) is not BaseMethodDeclarationSyntax declaration) continue;

				SyntaxNode? body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;

				if (body is null) continue;

				foreach (var identifier in DeclaredIn(body))
				{
					if (!added.Contains(identifier.ValueText)) continue;

					var line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

					return $"The new parameter '{identifier.ValueText}' would clash with the '{identifier.ValueText}' that "
						+ $"{SymbolAddress.Of(member)} already declares in its body, at "
						+ $"{Path.GetFileName(identifier.SyntaxTree?.FilePath)}:{line} -- CS0136 or CS1931, since neither a "
						+ "local nor a query's range variable may share a parameter's name. Nothing was written. Pick "
						+ "another name for the parameter, or rename the local first with rose_rename_symbol at that "
						+ "position.";
				}
			}
		}

		return null;
	}

	/// <summary>
	/// The names a body declares that a parameter of the same name would clash with: its locals, its
	/// pattern and out variables, its loop and catch variables, its query range variables, and the names
	/// of the local functions inside it.
	/// <para>
	/// Not what a lambda or a local function declares inside itself. Those may shadow a parameter of
	/// the member around them, so <c>Select(project => project.Name)</c> in a method gaining a
	/// <c>project</c> parameter compiles as it stands, and refusing it would refuse a valid change. A
	/// query's range variables may not, and <c>from item in items</c> is CS1931 the moment a parameter
	/// <c>item</c> lands.
	/// </para>
	/// </summary>
	private static IEnumerable<SyntaxToken> DeclaredIn(SyntaxNode body)
	{
		var nodes = body.DescendantNodes(node =>
			node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax);

		foreach (var node in nodes)
		{
			var declared = node switch
			{
				VariableDeclaratorSyntax variable => variable.Identifier,
				SingleVariableDesignationSyntax designation => designation.Identifier,
				ForEachStatementSyntax loop => loop.Identifier,
				CatchDeclarationSyntax caught => caught.Identifier,
				LocalFunctionStatementSyntax function => function.Identifier,
				FromClauseSyntax from => from.Identifier,
				LetClauseSyntax let => let.Identifier,
				JoinClauseSyntax join => join.Identifier,
				JoinIntoClauseSyntax into => into.Identifier,
				QueryContinuationSyntax continuation => continuation.Identifier,
				_ => default,
			};

			if (declared.IsKind(SyntaxKind.IdentifierToken)) yield return declared;
		}
	}

	/// <summary>
	/// The interface members a method implements, explicitly or by matching. Walked by hand because
	/// the implicit half is not a property on the symbol: it is a question for the containing type.
	/// </summary>
	private static IEnumerable<IMethodSymbol> InterfaceMembers(IMethodSymbol method)
	{
		foreach (var declared in method.ExplicitInterfaceImplementations) yield return declared;

		if (method.ContainingType is not { } type) yield break;

		foreach (var candidate in type.AllInterfaces.SelectMany(@interface => @interface.GetMembers(method.Name)))
		{
			if (candidate is IMethodSymbol member
				&& SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
			{
				yield return member;
			}
		}
	}

	/// <summary>
	/// The declarations whose accessibility has to change together, each with the accessibility it gets:
	/// the member, what it overrides all the way up, and everything overriding any of those.
	/// <para>
	/// Narrower than <see cref="GroupAsync"/>, which also follows interfaces. An override has to keep
	/// the accessibility of what it overrides, or it is CS0507, so the chain moves as one. An interface
	/// member and its implementations do not: an implementation is public or it is not one, so a
	/// change that would make it anything else is refused rather than written.
	/// </para>
	/// <para>
	/// One override can differ. A <c>protected internal</c> member overridden from another assembly is
	/// overridden as <c>protected</c>, because the internal half does not reach across, and writing it as
	/// declared is CS0507 again.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<AccessChange>> AccessGroupAsync(
		Solution solution,
		DeclarationTarget target,
		Accessibility wanted,
		CancellationToken cancellationToken)
	{
		if (AccessibilityModifiers.WhyRefused(target.Symbol, target.Declaration, wanted) is { } refusal)
		{
			throw new ArgumentException(refusal);
		}

		var root = target.Symbol;
		while (AccessibilityModifiers.Overridden(root) is { } above) root = above;

		var chain = new List<ISymbol>();
		var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		var pending = new Queue<ISymbol>([root]);

		while (pending.TryDequeue(out var next))
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!seen.Add(next)) continue;

			chain.Add(next);

			var overridable = next.IsVirtual || next.IsAbstract || next.IsOverride;
			if (!overridable) continue;

			foreach (var over in await SymbolFinder.FindOverridesAsync(next, solution, cancellationToken: cancellationToken))
			{
				pending.Enqueue(over);
			}
		}

		if (wanted != Accessibility.Public)
		{
			foreach (var member in chain)
			{
				if (AccessibilityModifiers.ImplicitlyImplemented(member) is not { } implemented) continue;

				throw new ArgumentException(
					$"{SymbolSignature.Of(member)} implements {SymbolSignature.Of(implemented)}, and does that only while it is "
						+ "public: anything narrower stops it implementing the interface, which is CS0737. Implement the "
						+ "interface member explicitly to keep it off the type's own surface instead.");
			}
		}

		return
		[
			.. chain.Select(member =>
			{
				var acrossAssemblies = wanted == Accessibility.ProtectedOrInternal
					&& !SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, root.ContainingAssembly);

				return new AccessChange(member, acrossAssemblies ? Accessibility.Protected : wanted);
			}),
		];
	}

	/// <summary>
	/// Which nodes in which documents have to change, worked out before anything is written. The call
	/// sites are looked for only when the parameters change: accessibility changes no argument, and what
	/// it does to a use is something only the compile afterwards can say.
	/// </summary>
	private static async Task<IReadOnlyList<DocumentWork>> GatherAsync(
		Solution solution,
		IReadOnlyList<IMethodSymbol> group,
		IReadOnlyList<AccessChange> access,
		ISymbol primarySymbol,
		ParameterPlan plan,
		SeparatedSyntaxList<ParameterSyntax> wanted,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var work = new Dictionary<DocumentId, DocumentWork>();

		DocumentWork For(Document document)
		{
			if (!work.TryGetValue(document.Id, out var found)) work[document.Id] = found = new DocumentWork(document.Id);

			return found;
		}

		var parameterGroup = group.ToHashSet<ISymbol>(SymbolEqualityComparer.Default);
		var accessFor = access.ToDictionary(change => change.Symbol, change => change.Accessibility, SymbolEqualityComparer.Default);
		var symbols = group.Concat(access.Select(change => change.Symbol)).Distinct(SymbolEqualityComparer.Default);

		foreach (var symbol in symbols)
		{
			var primary = SymbolEqualityComparer.Default.Equals(symbol, primarySymbol);

			foreach (var reference in symbol.DeclaringSyntaxReferences)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (DeclarationOf(await reference.GetSyntaxAsync(cancellationToken)) is not { } declaration) continue;
				if (solution.GetDocument(reference.SyntaxTree) is not { } document) continue;

				var reshaped = parameterGroup.Contains(symbol) && ParameterLists.Of(declaration) is not null;
				if (!reshaped && !accessFor.ContainsKey(symbol)) continue;

				var change = reshaped ? ChangeFor(declaration, plan, wanted, primary, notices) : new DeclarationChange();
				var found = For(document);

				if (reshaped) found.Asked.Add(ParameterLists.Of(declaration)!.Span);

				if (accessFor.TryGetValue(symbol, out var accessibility))
				{
					change = change with { Accessibility = AccessibilityModifiers.KeywordsFor(accessibility) };
					found.Asked.Add(AccessibilityModifiers.SpanOf(declaration));
				}

				found.Declarations[declaration.Span] = change;
				found.DeclarationSites.Add(declaration.GetLocation());

				if (change.Documentation is not null) found.Asked.Add(TextSpan.FromBounds(declaration.FullSpan.Start, declaration.SpanStart));
			}
		}

		foreach (var symbol in group)
		{
			foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
			{
				foreach (var location in reference.Locations)
				{
					cancellationToken.ThrowIfCancellationRequested();

					if (location.IsImplicit) continue;

					var document = location.Document;
					var root = await document.GetSyntaxRootAsync(cancellationToken);
					if (root is null) continue;

					var token = root.FindToken(location.Location.SourceSpan.Start);
					var found = For(document);

					if (ArgumentListOf(token) is not { } arguments)
					{
						found.Unusable.Add(new RefusedCallSite(location.Location, WhyUnusable(token)));
						continue;
					}

					found.CallSites.Add(arguments.Span);
					found.Asked.Add(arguments.Span);
					found.CallSiteLocations[arguments.Span] = location.Location;
				}
			}
		}

		return [.. work.Values];
	}

	/// <summary>
	/// The declaration a symbol's syntax belongs to. A field's own syntax is its variable declarator,
	/// and the modifiers that say who may see it are on the field declaration around that.
	/// </summary>
	private static MemberDeclarationSyntax? DeclarationOf(SyntaxNode node) => node switch
	{
		VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } => field,
		MemberDeclarationSyntax member => member,
		_ => null,
	};

	/// <summary>
	/// One declaration's new parameter list, and its documentation when that had to move too.
	/// <para>
	/// The declaration the caller named gets exactly the parameters they wrote. Every other
	/// declaration of the same member is mapped by position instead, keeping its own parameter names
	/// and attributes and taking only the change of type -- because an override is free to call its
	/// parameters something else, and replacing its list wholesale would rename them without saying
	/// so.
	/// </para>
	/// <para>
	/// The layout is the file's unless the caller wrote one. A list written on one line says what the
	/// parameters are and nothing about where they go, so every parameter that was there keeps its own
	/// line, indentation and comments, a new one takes the line of the parameter before it, and the
	/// commas between them are the file's -- the way a call site keeps its arguments. Rebuilt from the
	/// caller's text instead, a constructor wrapped one parameter to a line collapses into one line of
	/// two hundred characters, and the comments grouping its parameters go without trace. A list the
	/// caller wrapped is a layout they chose, and is used; the comments above its parameters are still
	/// the file's, since a comment is not layout.
	/// </para>
	/// </summary>
	private static DeclarationChange ChangeFor(
		SyntaxNode declaration,
		ParameterPlan plan,
		SeparatedSyntaxList<ParameterSyntax> wanted,
		bool primary,
		List<string> notices)
	{
		var list = ParameterLists.Of(declaration)!;
		var own = list.Parameters;
		var built = new List<ParameterSyntax>(plan.Parameters.Count);

		var callerWraps = primary
			&& wanted.GetWithSeparators().Any(item => item.ToFullString().Contains('\n', StringComparison.Ordinal));

		foreach (var parameter in plan.Parameters)
		{
			if (parameter.WasAt is { } at && at < own.Count)
			{
				var mine = own[at];

				if (!primary)
				{
					var retyped = plan.Retyped.Contains(parameter.Name, StringComparer.Ordinal)
						&& parameter.Declaration.Type is { } type;

					built.Add(retyped ? mine.WithType(parameter.Declaration.Type!) : mine);
					continue;
				}

				built.Add(callerWraps
					? WithComments(parameter.Declaration, mine)
					: parameter.Declaration.WithLeadingTrivia(mine.GetLeadingTrivia()).WithTrailingTrivia(mine.GetTrailingTrivia()));

				continue;
			}

			built.Add(callerWraps ? parameter.Declaration : Beside(parameter.Declaration, built, own));
		}

		var kept = plan.Parameters
			.Where(parameter => parameter.WasAt is not null)
			.Select(parameter => parameter.WasAt!.Value)
			.ToHashSet();

		var removedHere = own
			.Where((_, index) => !kept.Contains(index))
			.Select(parameter => parameter.Identifier.Text)
			.ToArray();

		var addedHere = plan.Added.Select(parameter => parameter.Name).ToArray();

		var keptHere = own
			.Where((_, index) => kept.Contains(index))
			.Select(parameter => parameter.Identifier.Text)
			.ToArray();

		var documentation = ParamTags.Update(declaration.GetLeadingTrivia(), removedHere, addedHere, keptHere, notices);
		var parameters = list.WithParameters(Separated(built, callerWraps ? wanted : own, ExtraSeparator(list)));

		return new DeclarationChange
		{
			Parameters = callerWraps ? parameters.WithOpenParenToken(Unbroken(parameters.OpenParenToken)) : parameters,
			Documentation = documentation,
		};
	}

	/// <summary>
	/// Rebuilds the separated list, keeping the commas that are already there so a parameter list
	/// somebody wrapped across lines stays wrapped, and giving any comma past them
	/// <paramref name="extra"/>.
	/// </summary>
	private static SeparatedSyntaxList<ParameterSyntax> Separated(
		IReadOnlyList<ParameterSyntax> parameters,
		SeparatedSyntaxList<ParameterSyntax> pattern,
		SyntaxToken extra)
	{
		if (parameters.Count <= 1) return SyntaxFactory.SeparatedList(parameters);

		var existing = pattern.GetSeparators().ToArray();
		var separators = new List<SyntaxToken>(parameters.Count - 1);

		for (var index = 0; index < parameters.Count - 1; index++)
		{
			separators.Add(index < existing.Length ? existing[index] : extra);
		}

		return SyntaxFactory.SeparatedList(parameters, separators);
	}

	/// <summary>
	/// The comma to put between parameters where the list has none to copy: its last one, or one ending
	/// the line where the list puts each parameter on a line of its own, or a comma and a space.
	/// <para>
	/// Whether the list wraps is read from the opening parenthesis and the parameters, never from the
	/// closing one: the line break after it is the signature ending, which every block-bodied member has.
	/// </para>
	/// </summary>
	private static SyntaxToken ExtraSeparator(ParameterListSyntax list)
	{
		var separators = list.Parameters.GetSeparators().ToArray();

		if (separators.Length > 0) return separators[^1];

		var lineEnding = list.OpenParenToken.TrailingTrivia
			.Concat(list.Parameters.SelectMany(parameter => parameter.GetLeadingTrivia()))
			.FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

		return lineEnding.IsKind(SyntaxKind.EndOfLineTrivia)
			? SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(lineEnding)
			: SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);
	}

	/// <summary>
	/// A new parameter laid out as the one before it is: at that indentation where the list wraps,
	/// inline where it does not. The first parameter of a list takes the indentation of the one it is
	/// going in front of.
	/// <para>
	/// The indentation alone, from after the neighbour's last line break. Its comment lines are its own,
	/// and copying the whitespace around them leaves a blank line where the comment was.
	/// </para>
	/// </summary>
	private static ParameterSyntax Beside(
		ParameterSyntax parameter,
		IReadOnlyList<ParameterSyntax> built,
		SeparatedSyntaxList<ParameterSyntax> own)
	{
		var neighbour = built.Count > 0 ? built[^1] : own.FirstOrDefault();

		var layout = neighbour is null
			? []
			: AfterLastBreak(neighbour.GetLeadingTrivia()).Where(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia));

		return parameter.WithLeadingTrivia(layout).WithTrailingTrivia();
	}

	/// <summary>
	/// The caller's parameter with the comment lines the file had above it, where there were any.
	/// Whatever layout the caller chose, a comment grouping parameters is not part of it, and a list
	/// rebuilt without it loses it without trace. The comment lines go between the caller's own line
	/// break and the caller's own indentation, so the layout around them is still the caller's.
	/// </summary>
	private static ParameterSyntax WithComments(ParameterSyntax written, ParameterSyntax existing)
	{
		var theirs = existing.GetLeadingTrivia();

		if (!theirs.Any(MemberSyntax.IsComment)) return written;

		var mine = written.GetLeadingTrivia();
		var indentation = AfterLastBreak(mine).ToArray();
		var commentLines = theirs.Take(theirs.Count - AfterLastBreak(theirs).Count());

		return written.WithLeadingTrivia(mine.Take(mine.Count - indentation.Length).Concat(commentLines).Concat(indentation));
	}

	/// <summary>The trivia after the last line break in a list, which is the indentation of the line it ends on.</summary>
	private static IEnumerable<SyntaxTrivia> AfterLastBreak(SyntaxTriviaList trivia)
	{
		var last = -1;

		for (var index = 0; index < trivia.Count; index++)
		{
			if (trivia[index].IsKind(SyntaxKind.EndOfLineTrivia)) last = index;
		}

		return trivia.Skip(last + 1);
	}

	/// <summary>
	/// The token with the whitespace after it dropped, keeping anything else.
	/// <para>
	/// The list the caller wrote carries its own layout from the parenthesis onwards, and the break
	/// the file had after that parenthesis is the file's idea of the same thing: a break on both
	/// makes a blank line, and a break on neither puts the first parameter inline with its own
	/// indentation in the middle of the signature. Only the declaration the caller named is re-laid
	/// out this way -- every other one keeps its own parameters, its own names and its own wrapping.
	/// </para>
	/// <para>
	/// Whitespace only, because a comment written between the parenthesis and the first parameter is
	/// not layout and would be lost without trace.
	/// </para>
	/// </summary>
	private static SyntaxToken Unbroken(SyntaxToken token) =>
		token.WithTrailingTrivia(token.TrailingTrivia.Where(trivia =>
			!trivia.IsKind(SyntaxKind.EndOfLineTrivia) && !trivia.IsKind(SyntaxKind.WhitespaceTrivia)));

	/// <summary>Applies every document's work, one rewrite per document.</summary>
	private static async Task<Applied> ApplyAsync(
		Solution solution,
		IReadOnlyList<DocumentWork> work,
		ParameterPlan plan,
		IReadOnlyDictionary<string, string> supplied,
		CancellationToken cancellationToken)
	{
		var rewritten = new List<Location>();
		var refused = new List<RefusedCallSite>();
		var documentation = new List<string>();

		// Every document is read from the solution as it arrived, not from the one being built up.
		// The spans in `work` were found there, and so was the binding each call site is rewritten
		// from: once the declaration has taken its new parameters, a call site that has not yet been
		// touched no longer binds to it, so a model read from the growing solution would refuse every
		// call site outside the file the declaration is in -- and the file order decides which ones.
		var original = solution;

		foreach (var item in work)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (original.GetDocument(item.Id) is not { } document) continue;
			if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) continue;
			if (await document.GetSemanticModelAsync(cancellationToken) is not { } model) continue;

			var marker = new SyntaxAnnotation();
			var rewriter = new SignatureRewriter(model, item.Declarations, item.CallSites, plan, supplied, marker);

			if (rewriter.Visit(root) is not { } updated) continue;

			foreach (var span in rewriter.Rewritten)
			{
				if (item.CallSiteLocations.TryGetValue(span, out var location)) rewritten.Add(location);
			}

			foreach (var (span, reason) in rewriter.Refused)
			{
				if (item.CallSiteLocations.TryGetValue(span, out var location))
				{
					refused.Add(new RefusedCallSite(location, reason));
				}
			}

			if (item.Declarations.Values.Any(change => change.Documentation is not null))
			{
				documentation.Add(document.FilePath ?? document.Name);
			}

			var rules = await Whitespace.RulesForAsync(document, cancellationToken);

			solution = await NormalisedAsync(solution, item.Id, updated, marker, rules, cancellationToken);
		}

		return new Applied(solution, rewritten, refused, documentation);
	}

	/// <summary>What the whole change asks of each file it rewrites, which is what its work recorded.</summary>
	private static Asked AskedOf(Solution solution, IReadOnlyList<DocumentWork> work) =>
		work.Aggregate(
			Asked.Nothing,
			(asked, item) => solution.GetDocument(item.Id) is { } document ? asked.And(document, item.Asked) : asked);

	/// <summary>
	/// The rewritten document with its written lines given the file's own whitespace, and every
	/// project holding the file given the same text.
	/// </summary>
	private static async Task<Solution> NormalisedAsync(
		Solution solution,
		DocumentId id,
		SyntaxNode updated,
		SyntaxAnnotation marker,
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		solution = solution.WithDocumentSyntaxRoot(id, updated);

		if (solution.GetDocument(id) is not { } document) return solution;

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var text = await document.GetTextAsync(cancellationToken);

		if (root is null || tree is null) return solution;

		var spans = root.GetAnnotatedNodesAndTokens(marker).Select(written => written.FullSpan).ToArray();
		if (spans.Length == 0) return solution;

		var final = Whitespace.Apply(root, text, rules, spans);

		if (document.FilePath is not { Length: > 0 } path) return solution.WithDocumentText(id, final);

		foreach (var linked in solution.GetDocumentIdsWithFilePath(path))
		{
			solution = solution.WithDocumentText(linked, final);
		}

		return solution;
	}

	/// <summary>
	/// The call sites that were left alone, each with the reason. The two reasons are different in
	/// kind: one is "nothing needed doing", which is a fact, and the other is "nothing could safely
	/// be done", which is a decision the caller now has to make.
	/// </summary>
	private static async Task<IReadOnlyList<UnchangedCallSite>> DescribeUnchangedAsync(
		Solution solution,
		IReadOnlyList<DocumentWork> work,
		Applied applied,
		ParameterPlan plan,
		CancellationToken cancellationToken)
	{
		var unchanged = new List<UnchangedCallSite>();

		// A call site belongs in one list and one only. The blanket loop at the bottom used to report
		// every call site there was, including the ones just rewritten and the ones refused -- so a
		// site the tool had only just changed also appeared as one that "needed no change", giving a
		// reason that was not what happened to it. #59 hit both halves at once: the same location was
		// listed as updated and as unchanged, and the refusal that is now the honest answer for it
		// would have been drowned by the same duplicate.
		var reported = new HashSet<Location>(applied.RewrittenCallSites);

		foreach (var refused in applied.RefusedCallSites)
		{
			reported.Add(refused.Location);

			unchanged.Add(new UnchangedCallSite
			{
				Location = await SymbolLocator.DescribeAsync(solution, refused.Location, cancellationToken),
				Reason = $"Its arguments were left exactly as written, because {refused.Reason}. Nothing here can "
					+ "put them back safely, so change it by hand.",
			});
		}

		foreach (var refused in work.SelectMany(item => item.Unusable))
		{
			reported.Add(refused.Location);

			unchanged.Add(new UnchangedCallSite
			{
				Location = await SymbolLocator.DescribeAsync(solution, refused.Location, cancellationToken),
				Reason = $"It was left exactly as written, because {refused.Reason}.",
			});
		}

		// The ones that compile either way, which is where the silent bug lives: a new parameter every
		// caller takes the default of, or an argument that converts to a parameter's new type. A change
		// that brings neither -- one that only says whether a parameter may be null -- leaves nothing at
		// a call site worth a look, and listing every one of them is a list nobody reads.
		var added = plan.Added.Any();
		var converted = plan.Converted.ToArray();

		if (plan.CallSitesUnaffected && (added || converted.Length > 0))
		{
			foreach (var item in work)
			{
				foreach (var location in item.CallSiteLocations.Values)
				{
					if (!reported.Add(location)) continue;

					var reason = added
						? await ForwarderAsync(solution, location, cancellationToken)
							?? "Nothing needed changing, since every new parameter has a default. Worth a look all "
							+ "the same: a caller that goes on taking the default may be one that should not."
						: $"Its arguments were left as they were, and the one it passes to {string.Join(", ", converted)} "
							+ "now goes to a different type. Worth a look: a conversion that happens to exist compiles "
							+ "and may mean something else.";

					unchanged.Add(new UnchangedCallSite
					{
						Location = await SymbolLocator.DescribeAsync(solution, location, cancellationToken),
						Reason = reason,
					});
				}
			}
		}

		return unchanged;
	}

	/// <summary>
	/// The reason to give for a call site that is the whole body of a forwarder, or null where it is
	/// an ordinary call and the ordinary reason will do.
	/// <para>
	/// This is the shape the tool exists for and the one it cannot finish. A new parameter with a
	/// default breaks nothing, so the forwarder compiles and goes on passing the default -- and every
	/// caller of it silently gets the behaviour the change was meant to alter. Listing it beside forty
	/// ordinary call sites is what let a five-deep chain go half-changed.
	/// </para>
	/// </summary>
	private static async Task<string?> ForwarderAsync(
		Solution solution,
		Location location,
		CancellationToken cancellationToken)
	{
		if (location.SourceTree is not { } tree) return null;

		var root = await tree.GetRootAsync(cancellationToken);
		var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);

		if (Forwarders.Around(node) is not { } member) return null;

		var name = member is MethodDeclarationSyntax method ? method.Identifier.Text : "this member";

		return $"It is the whole body of {name}, which forwards its own parameters through. Nothing here "
			+ "needed changing, and that is the problem: the forwarder compiles while still passing the "
			+ "old default, so its own callers get the behaviour this change was meant to alter. Change "
			+ "its signature too, and then whatever calls it.";
	}

	private static async Task<IReadOnlyList<SourceLocation>> DescribeAsync(
		Solution solution,
		IEnumerable<Location> locations,
		CancellationToken cancellationToken)
	{
		var described = new List<SourceLocation>();

		foreach (var location in locations)
		{
			described.Add(await SymbolLocator.DescribeAsync(solution, location, cancellationToken));
		}

		return described;
	}

	private static IEnumerable<string> Notices(
		ChangeSignatureRequest request,
		ParameterPlan plan,
		Applied applied,
		Verification verification,
		WriteOutcome outcome,
		IReadOnlyList<UnchangedCallSite> unchanged,
		ISymbol symbol,
		IReadOnlyList<AccessChange> access)
	{
		// First, because it is the only thing here that is nobody's work but this tool's.
		foreach (var defect in Defects(applied, verification)) yield return defect;

		if (!request.Apply) yield return "Preview only; nothing was written to disk.";
		if (outcome.ChangedFiles.Count == 0) yield return "The signature already read exactly like that.";

		if (access.Count > 0 && AccessibilityModifiers.Spelled(symbol.DeclaredAccessibility) is { } was)
		{
			var moved = access.Count - 1;

			yield return $"It was {was}."
				+ (moved > 0
					? $" {moved} declaration(s) it overrides or that override it moved with it, since an override has to "
						+ "keep the accessibility of what it overrides."
					: string.Empty);
		}

		if (request.Accessibility is { } asked)
		{
			var wanted = AccessibilityModifiers.Parse(asked);

			foreach (var change in access.Where(change => change.Accessibility != wanted))
			{
				yield return $"{SymbolSignature.Of(change.Symbol)} is {AccessibilityModifiers.Spelled(change.Accessibility)} "
					+ $"rather than {AccessibilityModifiers.Spelled(wanted)}: it overrides from another assembly, which "
					+ "the internal half does not reach.";
			}
		}

		// What the diff could not show, which for a change reaching several files is worth saying
		// before anything about what compiled.
		foreach (var notice in outcome.Notices) yield return notice;

		if (plan.Converted.Any())
		{
			yield return $"Retyped {string.Join(", ", plan.Converted)}, which the call sites still pass their old "
				+ "arguments to. A conversion that happens to exist will compile and mean something different.";
		}

		if (plan.Reannotated.Count > 0)
		{
			yield return $"Changed only whether {string.Join(", ", plan.Reannotated)} may be null, which no argument "
				+ "has to change for. A caller passing null where it no longer may is a nullable warning, and the "
				+ "compile reports it only where those are errors.";
		}

		if (unchanged.Count > 0)
		{
			yield return $"{unchanged.Count} use(s) were left as they were; each says why.";
		}

		if (!verification.Ran)
		{
			if (outcome.ChangedFiles.Count > 0)
			{
				yield return "Nothing was compiled, so this says nothing about what the change broke. Pass "
					+ "verify=true, or ask rose_diagnostics with scope=solution.";
			}

			yield break;
		}

		foreach (var notice in verification.Notices) yield return notice;

		// What this change did, in prose, as every writing tool reports it. A result that silently
		// stopped at twenty entries reads as a change that broke twenty things.
		if (verification.Introduced.Count > 0)
		{
			yield return verification.Introduced.Count > EditPipeline.Listed
				? $"This introduced {verification.Introduced.Count} error(s) in the solution; the first "
					+ $"{EditPipeline.Listed} are listed."
				: $"This introduced {verification.Introduced.Count} error(s) in the solution.";
		}

		if (verification.TotalCount == 0) yield return "The whole solution compiles clean.";

		var existing = verification.TotalCount - verification.Introduced.Count;

		if (existing > 0)
		{
			yield return $"{existing} error(s) in the solution were there before this change.";
		}

		// A new or retyped parameter names a type, and the declaration's file is as likely to be
		// missing the import for it as any other. This tool writes to files it was never pointed at,
		// so the caller has no reason to have thought about their imports at all.
		foreach (var suggestion in verification.Suggestions) yield return suggestion;
	}

	/// <summary>
	/// The one thing in a result that is this tool's own fault, said as such.
	/// <para>
	/// An argument-mapping error where this rewrote a call site cannot be the caller's work, so
	/// listing it beside the errors that are would send them looking for it in code they did not
	/// write. <see cref="CallSiteBinding.MappingFailures"/> decides which those are.
	/// </para>
	/// </summary>
	private static IEnumerable<string> Defects(Applied applied, Verification verification)
	{
		var files = applied.RewrittenCallSites
			.Select(location => location.SourceTree?.FilePath)
			.OfType<string>();

		var mapping = CallSiteBinding.MappingFailures(files, verification.Introduced);

		if (mapping.Count == 0) yield break;

		var listed = string.Join(", ", mapping.Select(Where));

		yield return $"This is a defect in rose_change_signature rather than in your code: {listed}. Each of those "
			+ "is the compiler saying an argument does not fit the parameter it was written for, at a call site "
			+ "whose arguments this rewrote a moment ago. Revert those files and report it.";
	}

	/// <summary>One diagnostic as a place, short enough to list several of on one line.</summary>
	private static string Where(DiagnosticEntry diagnostic) =>
		$"{diagnostic.Id} at {Path.GetFileName(diagnostic.FilePath)}:{diagnostic.Line}";

	/// <summary>
	/// The argument list of the call this reference is the target of, or nothing when the reference
	/// is not a call at all.
	/// <para>
	/// The containment check is what stops <c>Register(Handler)</c> -- a method group passed as an
	/// argument -- from being read as a call to <c>Register</c> and having its arguments rewritten
	/// for a change to <c>Handler</c>.
	/// </para>
	/// </summary>
	private static ArgumentListSyntax? ArgumentListOf(SyntaxToken token)
	{
		for (var node = token.Parent; node is not null; node = node.Parent)
		{
			switch (node)
			{
				case ArgumentSyntax or AttributeSyntax or StatementSyntax or MemberDeclarationSyntax:
					return null;

				case InvocationExpressionSyntax invocation when invocation.Expression.Span.Contains(token.Span):
					return invocation.ArgumentList;

				case ObjectCreationExpressionSyntax creation when creation.Type.Span.Contains(token.Span):
					return creation.ArgumentList;
			}
		}

		return null;
	}

	/// <summary>
	/// Why a reference cannot have its arguments rewritten, as a clause naming the shape.
	/// <para>
	/// Two different things, and calling them one was wrong about half of them. A nameof, a cref and
	/// a method group name the member without calling it, so there are no arguments to put back at
	/// all. A base or this initialiser <em>is</em> a call, with arguments, that the walk from a
	/// reference to its invocation does not reach -- and telling someone their initialiser is not a
	/// call is a confident answer to a question they did not ask, on the line the compiler is about
	/// to fail on.
	/// </para>
	/// </summary>
	private static string WhyUnusable(SyntaxToken token)
	{
		for (var node = token.Parent; node is not null; node = node.Parent)
		{
			if (node is ConstructorInitializerSyntax)
			{
				return "it is a base or this initialiser -- a call to the constructor that does not go through an "
					+ "invocation, which is what this rewrites arguments in, so its arguments have to be changed "
					+ "by hand";
			}

			if (node is MemberDeclarationSyntax) break;
		}

		return "it names the member without calling it -- a method group, a nameof, or a cref -- so it has no "
			+ "arguments to put back, and a changed signature can break it with nothing here able to help";
	}
	private static IReadOnlyDictionary<string, string> Supplied(IReadOnlyList<string> arguments)
	{
		var supplied = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var argument in arguments)
		{
			var split = argument.IndexOf('=');

			if (split <= 0)
			{
				throw new ArgumentException(
					$"'{argument}' is not an argument for a parameter. Write each one as name=expression, for "
						+ "example loud=false.");
			}

			supplied[argument[..split].Trim()] = argument[(split + 1)..].Trim();
		}

		return supplied;
	}

	/// <summary>
	/// Refuses before anything is written when a new parameter has neither a default nor an argument
	/// to pass, and there is a call site the refusal is true of.
	/// <para>
	/// It has to be asked of the call sites rather than of the plan alone, which is what made it fire
	/// on the two solutions where it is false. A call written ahead of the parameter it passes -- an
	/// author's ordinary way round -- does not bind, so nothing can put an argument into it and this
	/// change is what it was waiting for; and a member nothing calls has no call site to break at all.
	/// Both were refused with "the existing call sites have nothing to pass", which named sites that
	/// had something to pass and sites that did not exist, and recommended an argument that would have
	/// been written into neither.
	/// </para>
	/// </summary>
	/// <param name="plan">What is happening to the parameters.</param>
	/// <param name="supplied">Expressions the caller gave for new parameters, by name.</param>
	/// <param name="binding">
	/// How many call sites the compiler can still read, which is how many would stop compiling.
	/// </param>
	private static void GuardMissingArguments(
		ParameterPlan plan,
		IReadOnlyDictionary<string, string> supplied,
		int binding)
	{
		var missing = plan.Added
			.Where(parameter => !parameter.HasDefault && !supplied.ContainsKey(parameter.Name))
			.Select(parameter => parameter.Name)
			.ToArray();

		if (missing.Length == 0 || binding == 0) return;

		throw new ArgumentException(
			$"{string.Join(", ", missing)} would be required, and {binding} call site(s) that compile today have "
				+ "nothing to pass. Give the parameter a default, or say what to pass with arguments as "
				+ "name=expression.");
	}

	/// <summary>
	/// How many call sites the compiler can still read, which is how many a required parameter with
	/// nothing to pass would break.
	/// <para>
	/// A site that does not bind is one where nothing is known about which argument means what, so
	/// nothing here can put an argument into it and it is left exactly as written either way. Two
	/// shapes arrive that way and only one is a problem: a call written before the parameter it passes
	/// -- which is what an author does, and which this change is what it was waiting for -- and a call
	/// that was already broken for some other reason. Neither can be given an argument, so neither is
	/// a reason to refuse, and both are reported.
	/// </para>
	/// <para>
	/// Read from the same model and the same spans <see cref="ApplyAsync"/> uses, so the guard and the
	/// rewrite cannot disagree about which sites are readable.
	/// </para>
	/// </summary>
	private static async Task<int> BindingCallSiteCountAsync(
		Solution solution,
		IReadOnlyList<DocumentWork> work,
		CancellationToken cancellationToken)
	{
		var count = 0;

		foreach (var item in work)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (item.CallSites.Count == 0) continue;
			if (solution.GetDocument(item.Id) is not { } document) continue;
			if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) continue;
			if (await document.GetSemanticModelAsync(cancellationToken) is not { } model) continue;

			foreach (var span in item.CallSites)
			{
				if (root.FindNode(span) is not ArgumentListSyntax arguments) continue;
				if (CallSiteBinding.For(model, arguments, out _) is null) continue;

				count++;
			}
		}

		return count;
	}

	/// <summary>Everything one document has to have done to it.</summary>
	private sealed record DocumentWork(DocumentId Id)
	{
		public Dictionary<TextSpan, DeclarationChange> Declarations { get; } = [];

		public List<Location> DeclarationSites { get; } = [];

		/// <summary>Argument lists to rewrite, by their span.</summary>
		public HashSet<TextSpan> CallSites { get; } = [];

		public Dictionary<TextSpan, Location> CallSiteLocations { get; } = [];

		/// <summary>Uses whose arguments this cannot rewrite, each with the reason.</summary>
		public List<RefusedCallSite> Unusable { get; } = [];

		/// <summary>
		/// What rewriting this document asks to change: the parameter lists and argument lists it
		/// rewrites, and the documentation above a declaration whose param tags move with them.
		/// </summary>
		public List<TextSpan> Asked { get; } = [];
	}

	/// <summary>
	/// A call site left exactly as written, and the reason, which is a clause naming the shape.
	/// Carried per site rather than answered once for all of them, because the reasons are different
	/// work for the caller: a call that never compiled may already be right after this change, while
	/// a shape the rewriter cannot spell has to be written by hand.
	/// </summary>
	private sealed record RefusedCallSite(Location Location, string Reason);

	/// <summary>One declaration whose accessibility changes, and what it changes to.</summary>
	private sealed record AccessChange(ISymbol Symbol, Accessibility Accessibility);

	private sealed record Applied(
		Solution Solution,
		IReadOnlyList<Location> RewrittenCallSites,
		IReadOnlyList<RefusedCallSite> RefusedCallSites,
		IReadOnlyList<string> Documentation);
}
