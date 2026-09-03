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
/// same capability and the same argument, and until now it was done by hand: in one session on this
/// repository a single optional parameter crossed six layers, each found by grep and edited by text
/// anchor, and a test call site was missed entirely and surfaced only as CS7036 from a build.
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
	private const int Listed = 20;

	public static async Task<MutationResult<SignatureChangeResult>> ChangeAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		ChangeSignatureRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		if (request.ExpectedRevision is { } expected && expected != snapshot.Revision)
		{
			throw new InvalidOperationException(
				$"The workspace is at revision {snapshot.Revision}, not the expected {expected}. "
					+ "Something changed underneath this request; re-read and try again.");
		}

		var notices = new List<string>(snapshot.Notices);

		progress?.Report($"Resolving {request.Symbol}", 0);

		var target = await DeclarationLocator.FindSymbolAsync(
			snapshot.Solution, request.Symbol, request.FilePath, cancellationToken);

		if (target.Symbol is not IMethodSymbol method || target.Declaration is not BaseMethodDeclarationSyntax primary)
		{
			throw new ArgumentException(
				$"{target.Signature} has no parameter list to change. This changes a method, a constructor or an "
					+ "operator; rose_replace_member writes any other declaration whole.");
		}

		var text = await target.Document.GetTextAsync(cancellationToken);
		var indent = IndentAt(text, primary.SpanStart);

		var wanted = MemberSyntax.ParseParameters(request.Parameters, target.Document.Project.ParseOptions, indent);
		var plan = ParameterPlan.For(primary.ParameterList.Parameters, wanted);

		if (plan.WhyImpossible() is { } refusal) throw new ArgumentException(refusal);

		var supplied = Supplied(request.Arguments);
		GuardMissingArguments(plan, supplied);

		progress?.Report("Finding the declarations that move with it", 10);

		var group = await GroupAsync(snapshot.Solution, method, cancellationToken);

		progress?.Report("Finding the call sites", 25);

		var work = await GatherAsync(snapshot.Solution, group, method, plan, wanted, notices, cancellationToken);

		progress?.Report("Rewriting", 55);

		var applied = await ApplyAsync(snapshot.Solution, work, plan, supplied, cancellationToken);

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 70);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, applied.Solution, request.Apply, noteSelfWrite, cancellationToken);

		var verification = Verification.NotRun;

		if (request.Verify && outcome.ChangedFiles.Count > 0)
		{
			progress?.Report("Compiling the solution to see what moved", 80);

			verification = await EditVerification.RunAsync(
				diagnostics,
				snapshot.Solution,
				applied.Solution,
				EditVerification.AllProjects(applied.Solution),
				target.FilePath,
				cancellationToken);
		}

		var unchanged = await DescribeUnchangedAsync(snapshot.Solution, work, applied, plan, cancellationToken);

		notices.AddRange(Notices(request, plan, verification, outcome, unchanged));

		var result = new SignatureChangeResult
		{
			Revision = snapshot.Revision,
			Symbol = target.Signature,
			Parameters = request.Parameters.Trim(),
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			UpdatedDeclarations = await DescribeAsync(snapshot.Solution, work.SelectMany(w => w.DeclarationSites), cancellationToken),
			UpdatedCallSites = await DescribeAsync(snapshot.Solution, applied.RewrittenCallSites, cancellationToken),
			UnchangedCallSites = unchanged,
			DocumentationUpdated = applied.Documentation,
			Verified = verification.Ran,
			IntroducedDiagnostics = [.. verification.Introduced.Take(Listed)],
			ResolvedDiagnosticCount = verification.ResolvedCount,
			TotalErrorCount = verification.TotalCount,
			ProjectsChecked = verification.Projects,
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		var changed = request.Apply && outcome.ChangedFiles.Count > 0 ? applied.Solution : null;

		return new MutationResult<SignatureChangeResult>(result, changed);
	}

	/// <summary>
	/// The declarations that have to change together: the member, the declaration it overrides or
	/// implements all the way up, and everything else that overrides or implements those.
	/// <para>
	/// Not optional. A virtual method whose override keeps the old parameters does not compile, and
	/// an interface member whose implementations keep theirs does not either -- so a tool that
	/// changed only what it was pointed at would break the build every time the member was virtual.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<IMethodSymbol>> GroupAsync(
		Solution solution,
		IMethodSymbol method,
		CancellationToken cancellationToken)
	{
		var roots = new List<IMethodSymbol>();

		for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
		{
			roots.Add(current);
			roots.AddRange(InterfaceMembers(current));
		}

		var group = new List<IMethodSymbol>(roots);

		foreach (var root in roots.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();

			group.AddRange((await SymbolFinder.FindOverridesAsync(root, solution, cancellationToken: cancellationToken))
				.OfType<IMethodSymbol>());

			group.AddRange((await SymbolFinder.FindImplementationsAsync(root, solution, cancellationToken: cancellationToken))
				.OfType<IMethodSymbol>());
		}

		return [.. group.Distinct(SymbolEqualityComparer.Default).OfType<IMethodSymbol>()];
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

	/// <summary>Which nodes in which documents have to change, worked out before anything is written.</summary>
	private static async Task<IReadOnlyList<DocumentWork>> GatherAsync(
		Solution solution,
		IReadOnlyList<IMethodSymbol> group,
		IMethodSymbol primarySymbol,
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

		foreach (var symbol in group)
		{
			var primary = SymbolEqualityComparer.Default.Equals(symbol, primarySymbol);

			foreach (var reference in symbol.DeclaringSyntaxReferences)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var node = await reference.GetSyntaxAsync(cancellationToken);
				if (node is not BaseMethodDeclarationSyntax declaration) continue;
				if (solution.GetDocument(reference.SyntaxTree) is not { } document) continue;

				var found = For(document);

				found.Declarations[declaration.Span] = ChangeFor(declaration, plan, wanted, primary, notices);
				found.DeclarationSites.Add(declaration.GetLocation());
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
						found.Unusable.Add(location.Location);
						continue;
					}

					var model = await document.GetSemanticModelAsync(cancellationToken);

					found.CallSites[arguments.Span] = Reduced(model, arguments.Parent) ? 1 : 0;
					found.CallSiteLocations[arguments.Span] = location.Location;
				}
			}
		}

		return [.. work.Values];
	}

	/// <summary>
	/// One declaration's new parameter list, and its documentation when that had to move too.
	/// <para>
	/// The declaration the caller named gets exactly the parameters they wrote. Every other
	/// declaration of the same member is mapped by position instead, keeping its own parameter names
	/// and attributes and taking only the change of type -- because an override is free to call its
	/// parameters something else, and replacing its list wholesale would rename them without saying
	/// so.
	/// </para>
	/// </summary>
	private static DeclarationChange ChangeFor(
		BaseMethodDeclarationSyntax declaration,
		ParameterPlan plan,
		SeparatedSyntaxList<ParameterSyntax> wanted,
		bool primary,
		List<string> notices)
	{
		var own = declaration.ParameterList.Parameters;
		var built = new List<ParameterSyntax>(plan.Parameters.Count);

		foreach (var parameter in plan.Parameters)
		{
			if (!primary && parameter.WasAt is { } at && at < own.Count)
			{
				var mine = own[at];
				var retyped = plan.Retyped.Contains(parameter.Name, StringComparer.Ordinal)
					&& parameter.Declaration.Type is { } type;

				built.Add(retyped ? mine.WithType(parameter.Declaration.Type!) : mine);
				continue;
			}

			built.Add(parameter.Declaration);
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

		var documentation = ParamTags.Update(declaration.GetLeadingTrivia(), removedHere, addedHere, notices);

		return new DeclarationChange
		{
			Parameters = declaration.ParameterList.WithParameters(
				Separated(built, primary ? wanted : own)),
			Documentation = documentation,
		};
	}

	/// <summary>
	/// Rebuilds the separated list, keeping the commas that are already there so a parameter list
	/// somebody wrapped across lines stays wrapped.
	/// </summary>
	private static SeparatedSyntaxList<ParameterSyntax> Separated(
		IReadOnlyList<ParameterSyntax> parameters,
		SeparatedSyntaxList<ParameterSyntax> pattern)
	{
		if (parameters.Count <= 1) return SyntaxFactory.SeparatedList(parameters);

		var existing = pattern.GetSeparators().ToArray();
		var separators = new List<SyntaxToken>(parameters.Count - 1);

		for (var index = 0; index < parameters.Count - 1; index++)
		{
			separators.Add(index < existing.Length
				? existing[index]
				: SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space));
		}

		return SyntaxFactory.SeparatedList(parameters, separators);
	}

	/// <summary>Applies every document's work, one rewrite per document.</summary>
	private static async Task<Applied> ApplyAsync(
		Solution solution,
		IReadOnlyList<DocumentWork> work,
		ParameterPlan plan,
		IReadOnlyDictionary<string, string> supplied,
		CancellationToken cancellationToken)
	{
		var rewritten = new List<Location>();
		var refused = new List<Location>();
		var documentation = new List<string>();

		foreach (var item in work)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (solution.GetDocument(item.Id) is not { } document) continue;
			if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root) continue;

			var marker = new SyntaxAnnotation();
			var rewriter = new SignatureRewriter(item.Declarations, item.CallSites, plan, supplied, marker);

			if (rewriter.Visit(root) is not { } updated) continue;

			foreach (var span in rewriter.Rewritten)
			{
				if (item.CallSiteLocations.TryGetValue(span, out var location)) rewritten.Add(location);
			}

			foreach (var span in rewriter.Refused)
			{
				if (item.CallSiteLocations.TryGetValue(span, out var location)) refused.Add(location);
			}

			if (item.Declarations.Values.Any(change => change.Documentation is not null))
			{
				documentation.Add(document.FilePath ?? document.Name);
			}

			solution = await NormalisedAsync(solution, item.Id, updated, marker, cancellationToken);
		}

		return new Applied(solution, rewritten, refused, documentation);
	}

	/// <summary>
	/// The rewritten document with its written lines given the file's own whitespace, and every
	/// project holding the file given the same text.
	/// </summary>
	private static async Task<Solution> NormalisedAsync(
		Solution solution,
		DocumentId id,
		SyntaxNode updated,
		SyntaxAnnotation marker,
		CancellationToken cancellationToken)
	{
		solution = solution.WithDocumentSyntaxRoot(id, updated);

		if (solution.GetDocument(id) is not { } document) return solution;

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var text = await document.GetTextAsync(cancellationToken);

		if (root is null || tree is null) return solution;

		var spans = root.GetAnnotatedNodes(marker).Select(node => node.FullSpan).ToArray();
		if (spans.Length == 0) return solution;

		var rules = Whitespace.RulesFor(document.Project, tree, text);
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

		foreach (var location in applied.RefusedCallSites)
		{
			unchanged.Add(new UnchangedCallSite
			{
				Location = await SymbolLocator.DescribeAsync(solution, location, cancellationToken),
				Reason = "Its arguments could not be put back safely -- a params expansion, or an argument whose "
					+ "meaning depends on its position. Change this one by hand.",
			});
		}

		foreach (var location in work.SelectMany(item => item.Unusable))
		{
			unchanged.Add(new UnchangedCallSite
			{
				Location = await SymbolLocator.DescribeAsync(solution, location, cancellationToken),
				Reason = "It names the member without calling it -- a method group, a nameof, or a cref. A changed "
					+ "signature can break that, and nothing here can rewrite it.",
			});
		}

		// The ones that compile either way, which is where the silent bug lives.
		if (plan.CallSitesUnaffected)
		{
			foreach (var item in work)
			{
				foreach (var location in item.CallSiteLocations.Values)
				{
					unchanged.Add(new UnchangedCallSite
					{
						Location = await SymbolLocator.DescribeAsync(solution, location, cancellationToken),
						Reason = "Nothing needed changing, since every new parameter has a default. Worth a look all "
							+ "the same: a caller that goes on taking the default may be one that should not.",
					});
				}
			}
		}

		return unchanged;
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
		Verification verification,
		WriteOutcome outcome,
		IReadOnlyList<UnchangedCallSite> unchanged)
	{
		if (!request.Apply) yield return "Preview only; nothing was written to disk.";
		if (outcome.ChangedFiles.Count == 0) yield return "The signature already read exactly like that.";

		if (plan.Retyped.Count > 0)
		{
			yield return $"Retyped {string.Join(", ", plan.Retyped)}, which the call sites still pass their old "
				+ "arguments to. A conversion that happens to exist will compile and mean something different.";
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

		if (verification.TotalCount == 0) yield return "The whole solution compiles clean.";

		var existing = verification.TotalCount - verification.Introduced.Count;

		if (existing > 0)
		{
			yield return $"{existing} error(s) in the solution were there before this change.";
		}
	}

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
	/// True when the call is an extension method invoked on its receiver, in which case the first
	/// parameter has no argument at the call site and everything after it is one place to the left.
	/// </summary>
	private static bool Reduced(SemanticModel? model, SyntaxNode? invocation) =>
		invocation is not null
			&& model?.GetSymbolInfo(invocation).Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension };

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
	/// to pass. Every call site would fail to compile, and the caller knows which of the two they
	/// meant.
	/// </summary>
	private static void GuardMissingArguments(ParameterPlan plan, IReadOnlyDictionary<string, string> supplied)
	{
		var missing = plan.Added
			.Where(parameter => !parameter.HasDefault && !supplied.ContainsKey(parameter.Name))
			.Select(parameter => parameter.Name)
			.ToArray();

		if (missing.Length == 0) return;

		throw new ArgumentException(
			$"{string.Join(", ", missing)} would be required, and the existing call sites have nothing to pass. "
				+ "Give the parameter a default, or say what to pass with arguments as name=expression.");
	}

	private static string IndentAt(SourceText text, int position)
	{
		var line = text.Lines.GetLineFromPosition(position).ToString();

		return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
	}

	/// <summary>Everything one document has to have done to it.</summary>
	private sealed record DocumentWork(DocumentId Id)
	{
		public Dictionary<TextSpan, DeclarationChange> Declarations { get; } = [];

		public List<Location> DeclarationSites { get; } = [];

		/// <summary>Argument lists to rewrite, and how many parameters the call site does not write.</summary>
		public Dictionary<TextSpan, int> CallSites { get; } = [];

		public Dictionary<TextSpan, Location> CallSiteLocations { get; } = [];

		/// <summary>Uses that are not calls, so there is nothing to rewrite.</summary>
		public List<Location> Unusable { get; } = [];
	}

	private sealed record Applied(
		Solution Solution,
		IReadOnlyList<Location> RewrittenCallSites,
		IReadOnlyList<Location> RefusedCallSites,
		IReadOnlyList<string> Documentation);
}
