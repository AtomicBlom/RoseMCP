using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Semantic navigation: what a symbol is, where it is used, and finding it by name.</summary>
public static class NavigationService
{
	/// <summary>
	/// The most of a summary rose_symbol_info gives: several sentences, which is more than a summary is
	/// meant to hold. A longer one is remarks written into the summary, most often in a referenced
	/// assembly, and the cut is said in a notice so that it does not read as the whole.
	/// </summary>
	public const int MaxSummary = 1000;

	/// <summary>
	/// What a symbol is, and for a type from a referenced assembly, what can be called on it.
	/// </summary>
	/// <param name="snapshot">The solution to resolve in.</param>
	/// <param name="request">The symbol, named or pointed at.</param>
	/// <param name="cancellationToken">Cancels the read.</param>
	/// <param name="includeSource">Also return each declaration's source text.</param>
	/// <param name="members">
	/// For a metadata type, only members whose name contains this. Ignored, with a notice, for anything
	/// else.
	/// </param>
	/// <param name="maxMembers">For a metadata type, how many members to list at most.</param>
	/// <param name="used">How much of <paramref name="maxMembers"/> earlier symbols of the same call listed.</param>
	public static async Task<SymbolInfoResult> DescribeAsync(
		WorkspaceSnapshot snapshot,
		SymbolTarget request,
		CancellationToken cancellationToken,
		bool includeSource = false,
		string? members = null,
		int maxMembers = OutlineService.DefaultMaxMembers,
		int used = 0)
	{
		// The one read that can say something true about a symbol it cannot edit, so it is the one that
		// looks in metadata when nothing in source carries the name.
		var symbol = await request.ResolveAsync(snapshot, cancellationToken, includeMetadata: true);

		var declarations = new List<SourceLocation>();
		foreach (var location in symbol.Locations.Where(location => location.IsInSource))
		{
			declarations.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken));
		}

		var notices = new List<string>(snapshot.Notices);

		var written = DocumentationText.Summary(symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken));
		var summary = written is null ? null : DocumentationText.Bounded(written, MaxSummary);

		if (written is not null && summary!.Length < written.Length)
		{
			notices.Add($"The summary is {written.Length} characters, and the first {summary.Length} are given.");
		}

		// A metadata type has no file for rose_outline to read, so its members are listed here; a source
		// type is outline's, and listing it here too would be the same answer in two shapes.
		var listing = symbol is INamedTypeSymbol type && declarations.Count == 0
			? OutlineService.ListReachable(snapshot, type, members, maxMembers, cancellationToken, used)
			: null;

		if (listing is not null)
		{
			notices.AddRange(listing.Notices);
		}
		else if (!string.IsNullOrWhiteSpace(members) || maxMembers != OutlineService.DefaultMaxMembers)
		{
			notices.Add(declarations.Count > 0 && symbol is INamedTypeSymbol
				? "members and maxMembers list a referenced assembly's type, and this one is declared in source: rose_outline lists its members."
				: "members and maxMembers list a referenced assembly's type, and this is not a type, so they were ignored.");
		}

		return new SymbolInfoResult
		{
			Address = SymbolAddress.Of(symbol),
			Name = symbol.Name,
			Kind = symbol.Kind.ToString(),
			Signature = symbol.ToDisplayString(SymbolSignature.Format),
			Accessibility = symbol.DeclaredAccessibility.ToString(),
			ContainingType = symbol.ContainingType?.ToDisplayString(SymbolSignature.Format),
			Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false
				? symbol.ContainingNamespace.ToDisplayString()
				: null,
			Summary = summary,
			Declarations = declarations,
			DeclarationSpans = await SymbolLocator.SpansOfAsync(symbol, cancellationToken),
			BaseDefinitions = await DescribeAllAsync(snapshot, BaseDefinitions(symbol), cancellationToken),

			// A symbol from metadata has no source locations, which is also why it cannot be renamed. The
			// assembly it came from is named in its place, since that is the only place it can be looked at.
			IsFromSource = declarations.Count > 0,
			ContainingAssembly = declarations.Count > 0 ? null : symbol.ContainingAssembly?.Identity.Name,

			Members = listing?.Members,
			TotalMembers = listing?.Total,
			Truncated = listing?.Truncated ?? false,
			Notices = notices,

			Source = includeSource ? await SourceOfAsync(symbol, cancellationToken) : null,
		};
	}

	/// <summary>
	/// Every reference to a symbol: listed by file where there are few enough to read, and described by
	/// their shape where there are not.
	/// <para>
	/// A widely used member answers at a size nothing can read: one member of a test fixture came back
	/// at 63 KB. Cutting the list at <paramref name="maxResults"/> is no answer to it, because the first
	/// two hundred in path order are an arbitrary sample that reads as the whole answer, and the caller
	/// cannot tell what was cut. So past the cap the answer is the shape of the references instead --
	/// how many in each project, how many in tests and in generated code, which members hold them --
	/// and every group in it is a value one of the narrowing arguments takes, so the next call asks a
	/// smaller question rather than fetching the same answer somewhere else. Nothing is ever written to
	/// a file a caller did not name.
	/// </para>
	/// </summary>
	/// <param name="snapshot">The solution to search.</param>
	/// <param name="target">The symbol, named or pointed at.</param>
	/// <param name="maxResults">How many references to list at most before describing them instead.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	/// <param name="definitionsOnly">
	/// Return where the symbol is declared, how many uses there are and their shape, without listing
	/// them. The count is the whole answer to "is this used at all", and the shape to "is this safe to
	/// change", at a fraction of the size.
	/// </param>
	/// <param name="project">
	/// Only references compiled by this project. Named rather than filtered by the caller afterwards,
	/// because the truncation happens here: a symbol used five hundred times in tests and twice in the
	/// product answers with the two only if the narrowing reaches the search.
	/// </param>
	/// <param name="includePreviews">
	/// Whether each reference carries its line of source. The member each reference sits inside is
	/// reported either way, and that is what turns a flat list into "used by these six methods".
	/// </param>
	/// <param name="containingMember">
	/// Only references inside this member, as <c>Type.Member</c> or by the member's name alone.
	/// </param>
	/// <param name="isTestProject">True for only references in test projects, false for only those outside them.</param>
	/// <param name="isGenerated">True for only references in source-generated code, false for only those in files.</param>
	/// <param name="used">
	/// How many references earlier symbols of the same call already listed against <paramref name="maxResults"/>,
	/// which bounds the whole answer: this one lists only what is left of it.
	/// </param>
	/// <exception cref="ArgumentException">The solution has no project of that name.</exception>
	public static async Task<ReferencesResult> FindReferencesAsync(
		WorkspaceSnapshot snapshot,
		SymbolTarget target,
		int maxResults,
		CancellationToken cancellationToken,
		bool definitionsOnly = false,
		string? project = null,
		bool includePreviews = true,
		string? containingMember = null,
		bool? isTestProject = null,
		bool? isGenerated = null,
		int used = 0)
	{
		// Resolved before the search, so a name no project carries is refused rather than filtering every
		// reference out: an empty list reads exactly like a symbol nobody uses, which invites a deletion.
		var filter = new ReferenceFilter
		{
			Projects = project is { Length: > 0 }
				? ProjectNames.Resolve(snapshot.Solution, project).Select(candidate => candidate.Name).ToHashSet(StringComparer.Ordinal)
				: null,
			Project = project,
			ContainingMember = string.IsNullOrWhiteSpace(containingMember) ? null : containingMember.Trim(),
			IsTestProject = isTestProject,
			IsGenerated = isGenerated,
		};

		// Metadata included: who calls ILogger.LogInformation in this solution is a question about this
		// solution's source, and refusing it because nothing here declares the member answers a narrower
		// question than the one asked. The definitions come back empty, since a metadata symbol has no
		// source location, and the references are the answer.
		var symbol = await target.ResolveAsync(snapshot, cancellationToken, includeMetadata: true);
		var found = await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, cancellationToken);

		var definitions = new List<SourceLocation>();
		var references = new List<SourceLocation>();

		// The search cascades from a property to its accessors and its backing field, each a definition
		// of its own written inside the property's declaration, and listing each makes one property four
		// definitions on one line. A part is listed only when it is what was asked about.
		var defined = found.Select(reference => reference.Definition).ToHashSet(SymbolEqualityComparer.Default);

		foreach (var reference in found)
		{
			var isPartOfAnother = !SymbolEqualityComparer.Default.Equals(reference.Definition, symbol)
				&& DeclaredAsPartOf(reference.Definition) is { } whole
				&& defined.Contains(whole);

			var declared = isPartOfAnother
				? []
				: reference.Definition.Locations.Where(location => location.IsInSource).ToArray();

			foreach (var location in declared)
			{
				definitions.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken));
			}

			foreach (var location in reference.Locations)
			{
				if (location.Location.IsInSource)
				{
					references.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location.Location, cancellationToken));
				}
			}
		}

		var uses = InPlaceOrder(references.Distinct()).ToArray();

		// The search answers once for each copy of the symbol a multi-targeted project compiles, so one use
		// comes back once per framework of the declaring project. It is one use, counted once, and merged
		// after the filter so a project named with its framework keeps its own copy. Only copies of one
		// project merge: a file compiled by two projects is a use in each, and merging those would drop a
		// project from the shape and count the use on only one side of isTestProject.
		var samePlace = SamePlaceIn(snapshot.Solution);
		var filtered = filter.KeepsAll ? uses : uses.Where(filter.Over(uses).Keeps);
		var kept = filtered.DistinctBy(samePlace).ToArray();
		var every = uses.DistinctBy(samePlace).ToArray();

		// Which of four answers this is. A filter that kept nothing from a symbol that is used describes
		// every use instead, and says so: an empty list there reads as a symbol nobody uses, and the
		// unfiltered shape is what shows the caller which question does have an answer.
		var keptNothing = kept.Length == 0 && every.Length > 0;
		var room = Math.Max(0, maxResults - used);
		var overflows = !definitionsOnly && kept.Length > room;

		var notices = new List<string>();
		if (keptNothing) notices.Add(ReferenceShapes.NothingKept(every.Length, filter));
		else if (overflows) notices.Add(ReferenceShapes.Overflow(kept.Length, maxResults, used));

		var shape = keptNothing ? ReferenceShapes.Of(every)
			: (definitionsOnly || overflows) && kept.Length > 0 ? ReferenceShapes.Of(kept)
			: null;

		var listed = definitionsOnly || overflows ? [] : kept;

		return new ReferencesResult
		{
			Address = SymbolAddress.Of(symbol),
			Symbol = symbol.ToDisplayString(SymbolSignature.Format),

			// One declaration is still reached more than once: a positional record's property shares its
			// parameter's position, and a multi-targeted project compiles it once per framework. A file two
			// projects compile declares it in each, and each is listed.
			Definitions = [.. InPlaceOrder(definitions)
				.DistinctBy(samePlace)
				.Select(location => Previewed(location, includePreviews))],
			Files = ReferenceShapes.ByFile(listed, includePreviews),
			TotalCount = kept.Length,

			// Only an overflow is truncated: it is the one answer that raising maxResults changes.
			// definitionsOnly lists nothing because it was asked to, and asking again lists nothing again.
			Truncated = overflows,
			Shape = shape,
			Notices = notices,
		};
	}

	/// <summary>
	/// The symbol whose declaration this one is written inside: the property or event an accessor
	/// belongs to, or the property an automatic backing field stores.
	/// </summary>
	private static ISymbol? DeclaredAsPartOf(ISymbol symbol) => symbol switch
	{
		IMethodSymbol { AssociatedSymbol: { } whole } => whole,
		IFieldSymbol { AssociatedSymbol: { } whole } => whole,
		_ => null,
	};

	/// <summary>
	/// The location with or without its line of source. Dropping the preview is most of the size of a
	/// large answer, and it costs the caller nothing they cannot get back by asking about one file.
	/// </summary>
	private static SourceLocation Previewed(SourceLocation location, bool includePreviews) =>
		includePreviews ? location : location with { Preview = null };

	/// <summary>
	/// Locations in the order a reader meets them, and among copies of one location by project name, so
	/// the copy a merge keeps is the same one on every call rather than whichever the search reached first.
	/// </summary>
	private static IEnumerable<SourceLocation> InPlaceOrder(IEnumerable<SourceLocation> locations) =>
		locations
			.OrderBy(location => location.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(location => location.Line)
			.ThenBy(location => location.Column)
			.ThenBy(location => location.Project, StringComparer.Ordinal);

	/// <summary>
	/// What makes two locations one use or one declaration: the place in the file, and the project file
	/// that compiles it. MSBuild loads a multi-targeted project once per framework, each a Roslyn project
	/// of its own with the one project file, so their copies of a location merge; a file linked into two
	/// projects is compiled by each of them, and stays a location in each.
	/// </summary>
	private static Func<SourceLocation, (string FilePath, int Line, int Column, string? Project)> SamePlaceIn(Solution solution)
	{
		var projectFiles = solution.Projects
			.Where(project => project.FilePath is { Length: > 0 })
			.GroupBy(project => project.Name, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First().FilePath!, StringComparer.Ordinal);

		return location =>
		{
			var compiledBy = location.Project is { } name && projectFiles.TryGetValue(name, out var file)
				? file.ToUpperInvariant()
				: location.Project;

			return (location.FilePath.ToUpperInvariant(), location.Line, location.Column, compiledBy);
		};
	}

	/// <summary>
	/// What implements, overrides, or derives from the symbol at a position.
	/// <para>
	/// One tool rather than three, because the question a caller has is the same one -- "who else is
	/// involved in this" -- and which Roslyn call answers it is decided by what the symbol turns out
	/// to be. Answering the wrong question silently would be worse than answering none, so which one
	/// was answered is reported back.
	/// </para>
	/// <para>
	/// Only this solution's source is listed. Roslyn's search walks the referenced assemblies too, so a
	/// framework interface would be answered with every type in every dependency that implements it,
	/// and the cut would fall long before the first type declared here. For a type no project here
	/// declares, what this solution implements is the only form the question takes. What was left out
	/// is counted in a notice, so a short list does not read as the whole answer.
	/// </para>
	/// </summary>
	/// <param name="snapshot">The solution to search.</param>
	/// <param name="target">The symbol, named or pointed at.</param>
	/// <param name="maxResults">How many matches to return.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	/// <param name="project">
	/// Only matches compiled by this project. Applied before the cut, so the total and the truncation
	/// describe the narrowed list.
	/// </param>
	/// <exception cref="ArgumentException">The solution has no project of that name.</exception>
	public static async Task<ImplementationsResult> FindImplementationsAsync(
		WorkspaceSnapshot snapshot,
		SymbolTarget target,
		int maxResults,
		CancellationToken cancellationToken,
		string? project = null)
	{
		// Resolved before the search, so a name no project carries is refused rather than filtering every
		// match out: an empty list reads exactly like a type nothing implements.
		var narrowed = project is { Length: > 0 } ? ProjectNames.Resolve(snapshot.Solution, project) : null;

		// Metadata included for the target, and this is where it earns most: what in this solution
		// implements IDisposable or derives from Exception is a question about source, asked of a type no
		// project here declares, and it is the ordinary shape of the question rather than an edge of it.
		var symbol = await target.ResolveAsync(snapshot, cancellationToken, includeMetadata: true);
		var solution = snapshot.Solution;
		var found = new List<ISymbol>();

		var relationship = symbol switch
		{
			INamedTypeSymbol { TypeKind: TypeKind.Interface } => "types implementing this interface, and interfaces extending it",
			INamedTypeSymbol => "types derived from this one",

			// A member can be both overridden and an interface implementation, and a caller asking
			// about one usually wants the other too.
			_ => "members overriding or implementing this one",
		};

		// Searched from every copy, because the search from one copy finds only what derives from that
		// copy: a type one framework declares behind #if implements that framework's interface alone.
		foreach (var copy in await CopiesAsync(solution, symbol, cancellationToken))
		{
			found.AddRange(await RelatedAsync(solution, copy, cancellationToken));
		}

		var distinct = found.Distinct(SymbolEqualityComparer.Default).ToArray();
		var inSource = distinct.Where(IsInSource).ToArray();
		var compiled = narrowed is null ? inSource : await CompiledByAsync(narrowed, inSource, cancellationToken);

		// Keyed on the declaration rather than the symbol: a multi-targeted project compiles each type
		// once per framework, and the copies are different symbols for one line of source, so neither the
		// listing nor the counts may depend on how many of them the search handed back. The copy kept is
		// the one whose project sorts first, so a match names the same project on every call.
		var listed = compiled
			.OrderBy(candidate => ProjectNameOf(solution, candidate), StringComparer.Ordinal)
			.DistinctBy(Declaration)
			.ToArray();
		var inMetadata = distinct.Where(candidate => !IsInSource(candidate)).DistinctBy(Declaration).Count();
		var elsewhere = inSource.DistinctBy(Declaration).Count() - listed.Length;

		var matches = await DescribeAllAsync(snapshot, listed, cancellationToken);

		var ordered = matches
			.OrderBy(match => match.Signature, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var truncated = ordered.Length > maxResults;

		return new ImplementationsResult
		{
			Revision = snapshot.Revision,
			Address = SymbolAddress.Of(symbol),
			Symbol = symbol.ToDisplayString(SymbolSignature.Format),
			Relationship = relationship,
			Matches = truncated ? ordered[..maxResults] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
			Notices = [.. snapshot.Notices, .. LeftOut(inMetadata, elsewhere)],
		};
	}

	/// <summary>What implements, overrides, or derives from one symbol, by the search its kind calls for.</summary>
	private static async Task<IReadOnlyList<ISymbol>> RelatedAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
	{
		var found = new List<ISymbol>();

		if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } @interface)
		{
			found.AddRange(await SymbolFinder.FindImplementationsAsync(@interface, solution, cancellationToken: cancellationToken));
			found.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(@interface, solution, cancellationToken: cancellationToken));
		}
		else if (symbol is INamedTypeSymbol type)
		{
			found.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken));
		}
		else
		{
			found.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken));
			found.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken));
		}

		return found;
	}

	/// <summary>
	/// The symbol as each project compiling the file it is declared in declares it: once per framework of
	/// a multi-targeted project, and once in each project a linked file is compiled by, each a symbol of
	/// its own. A project whose <c>#if</c> leaves the declaration out has no copy. A symbol from a
	/// referenced assembly, or one written in no file a project lists, is its only copy.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> CopiesAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
	{
		var path = symbol.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
		if (path is not { Length: > 0 }) return [symbol];

		var copies = new List<ISymbol> { symbol };

		foreach (var projectId in solution.GetDocumentIdsWithFilePath(path).Select(document => document.ProjectId).Distinct())
		{
			if (solution.GetProject(projectId) is { } project && await CopyInAsync(project, symbol, cancellationToken) is { } copy)
			{
				copies.Add(copy);
			}
		}

		return [.. copies.Distinct(SymbolEqualityComparer.Default)];
	}

	private static bool IsInSource(ISymbol symbol) => symbol.Locations.Any(location => location.IsInSource);

	/// <summary>The project a symbol's first source declaration is compiled by, or its assembly where no document holds it.</summary>
	private static string? ProjectNameOf(Solution solution, ISymbol symbol)
	{
		var tree = symbol.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree;
		var document = tree is null ? null : solution.GetDocument(tree);

		return document?.Project.Name ?? symbol.ContainingAssembly?.Name;
	}

	/// <summary>
	/// What makes two symbols one declaration: the signature, and where it is written -- or, for a
	/// symbol from a referenced assembly, which assembly. The copies of a type that each framework of a
	/// multi-targeted project compiles are different symbols with one key.
	/// </summary>
	private static (string Signature, string? Where) Declaration(ISymbol symbol)
	{
		var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		var where = location is null
			? symbol.ContainingAssembly?.Identity.GetDisplayName()
			: $"{location.SourceTree?.FilePath}:{location.SourceSpan.Start}";

		return (symbol.ToDisplayString(SymbolSignature.Format), where);
	}

	/// <summary>
	/// Each candidate as one of the projects compiles it, leaving out the candidates none of them does.
	/// <para>
	/// Decided by whether a project's compilation declares the symbol, not by whether it compiles the file
	/// the candidate is written in. A multi-targeted project compiles each type once per framework and the
	/// search hands back one of the copies, so the copy found need not be the framework a caller named --
	/// but a file both frameworks compile can still declare a type for only one of them, behind
	/// <c>#if</c>, and the file alone would list it for both. The copy returned is the named project's own,
	/// so the match says the project the caller asked about. A declaration a generator wrote has no file
	/// to find, and is placed by the assembly it belongs to like any other.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> CompiledByAsync(
		IReadOnlyList<Project> projects,
		IReadOnlyList<ISymbol> candidates,
		CancellationToken cancellationToken)
	{
		var copies = new List<ISymbol>();

		foreach (var candidate in candidates)
		{
			foreach (var project in projects)
			{
				if (await CopyInAsync(project, candidate, cancellationToken) is { } copy)
				{
					copies.Add(copy);
					break;
				}
			}
		}

		return copies;
	}

	/// <summary>
	/// The symbol as this project declares it, or null where the project declares no such symbol.
	/// <para>
	/// Found by position first: the declaration written at the same place in this project's copy of the
	/// file. That reaches across projects with different assembly names, which a file linked into two of
	/// them has -- a shared project's items compiled by a UWP head and a desktop one -- where a symbol key
	/// carries the assembly name and resolves within one assembly only. A region <c>#if</c> leaves inactive
	/// for this project has no declaration at that place, so a type one framework compiles is not found in
	/// the other. Where nothing of the candidate's kind is declared at that place -- a declaration a
	/// generator wrote, in no file the project lists, or a positional record's property, whose syntax
	/// declares a parameter -- it is resolved by its symbol key instead. The key honours <c>#if</c> too,
	/// finding only what the compilation declares, and has to land in the project's own assembly: in a
	/// project that only references the declaring one, the key finds the type it uses and does not compile.
	/// </para>
	/// </summary>
	private static async Task<ISymbol?> CopyInAsync(Project project, ISymbol candidate, CancellationToken cancellationToken)
	{
		if (await project.GetCompilationAsync(cancellationToken) is not { } compilation) return null;
		if (SymbolEqualityComparer.Default.Equals(candidate.ContainingAssembly, compilation.Assembly)) return candidate;
		if (await DeclaredAtAsync(project, candidate, cancellationToken) is { } declared) return declared;

		return SymbolFinder.FindSimilarSymbols(candidate, compilation, cancellationToken)
			.FirstOrDefault(similar => SymbolEqualityComparer.Default.Equals(similar.ContainingAssembly, compilation.Assembly));
	}

	/// <summary>
	/// What this project declares at a place the candidate is written, of the candidate's kind and name;
	/// null where the project does not compile that file, or compiles it with the declaration inactive.
	/// </summary>
	private static async Task<ISymbol?> DeclaredAtAsync(Project project, ISymbol candidate, CancellationToken cancellationToken)
	{
		foreach (var reference in candidate.DeclaringSyntaxReferences)
		{
			var path = reference.SyntaxTree.FilePath;
			var id = string.IsNullOrEmpty(path)
				? null
				: project.Solution.GetDocumentIdsWithFilePath(path).FirstOrDefault(document => document.ProjectId == project.Id);

			if (id is null || project.GetDocument(id) is not { } document) continue;

			var root = await document.GetSyntaxRootAsync(cancellationToken);
			var model = await document.GetSemanticModelAsync(cancellationToken);
			var isWithinTheFile = root is not null && reference.Span.End <= root.FullSpan.End;
			if (!isWithinTheFile || model is null) continue;

			// The same text parsed with other symbols defined: where the declaration is active the node
			// spans exactly what it spans in the candidate's tree, and where it is not, the place is
			// disabled text inside some larger node.
			var declared = root!.FindNode(reference.Span, getInnermostNodeForTie: true)
				.AncestorsAndSelf()
				.TakeWhile(node => node.Span == reference.Span)
				.Select(node => model.GetDeclaredSymbol(node, cancellationToken))
				.FirstOrDefault(symbol => symbol is not null && symbol.Kind == candidate.Kind && symbol.Name == candidate.Name);

			if (declared is not null) return declared;
		}

		return null;
	}

	/// <summary>What the listing left out, counted, since a short list otherwise reads as the whole answer.</summary>
	private static IEnumerable<string> LeftOut(int inMetadata, int elsewhere)
	{
		if (inMetadata > 0)
		{
			yield return $"{inMetadata} more in referenced assemblies are not listed: only this solution's source is.";
		}

		if (elsewhere > 0)
		{
			yield return $"{elsewhere} more in projects other than the one named by project are not listed.";
		}
	}

	/// <summary>What a member overrides and what it implements, which is the same list to a caller.</summary>
	private static IReadOnlyList<ISymbol> BaseDefinitions(ISymbol symbol)
	{
		var bases = new List<ISymbol>();

		// A type's bases are the same question one level up, and were previously answered only for a
		// member -- so asking what a class derives from returned nothing at all.
		if (symbol is INamedTypeSymbol named)
		{
			if (named.BaseType is { SpecialType: not SpecialType.System_Object } super) bases.Add(super);

			bases.AddRange(named.Interfaces);

			return [.. bases.Distinct(SymbolEqualityComparer.Default)];
		}

		var overridden = symbol switch
		{
			IMethodSymbol method => method.OverriddenMethod,
			IPropertySymbol property => (ISymbol?)property.OverriddenProperty,
			IEventSymbol @event => @event.OverriddenEvent,
			_ => null,
		};

		if (overridden is not null) bases.Add(overridden);

		if (symbol.ContainingType is { } containing)
		{
			bases.AddRange(containing.AllInterfaces
				.SelectMany(@interface => @interface.GetMembers())
				.Where(member => SymbolEqualityComparer.Default.Equals(
					containing.FindImplementationForInterfaceMember(member), symbol)));
		}

		return [.. bases.Distinct(SymbolEqualityComparer.Default)];
	}

	/// <summary>
	/// The text of every declaration of a symbol, straight out of the tree it was parsed from.
	/// <para>
	/// The full span rather than the span alone, so the documentation comment and the attributes come
	/// with the member -- they are what a reader wanted the source for as often as the code is.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<string>> SourceOfAsync(ISymbol symbol, CancellationToken cancellationToken)
	{
		var written = new List<string>();

		foreach (var reference in symbol.DeclaringSyntaxReferences)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var node = await reference.GetSyntaxAsync(cancellationToken);

			written.Add(node.ToFullString().Trim());
		}

		return written;
	}

	private static async Task<IReadOnlyList<SymbolMatch>> DescribeAllAsync(
		WorkspaceSnapshot snapshot,
		IReadOnlyList<ISymbol> symbols,
		CancellationToken cancellationToken)
	{
		var matches = new List<SymbolMatch>();

		foreach (var symbol in symbols)
		{
			var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
			var document = location?.SourceTree is { } tree ? snapshot.Solution.GetDocument(tree) : null;

			matches.Add(new SymbolMatch
			{
				Name = symbol.Name,
				Kind = symbol.Kind.ToString(),
				Address = SymbolAddress.Of(symbol),
				Signature = symbol.ToDisplayString(SymbolSignature.Format),

				// Metadata symbols belong to no project in the solution, and saying so is more use
				// than an empty string that reads like a bug.
				Project = document?.Project.Name ?? symbol.ContainingAssembly?.Name ?? "(metadata)",
				Location = location is null
					? null
					: await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken),
			});
		}

		return matches;
	}

	/// <summary>
	/// Declarations whose names match a pattern, closest names first.
	/// <para>
	/// Past the cap the closest names are still listed, unlike a reference search's sample: the order is
	/// by how near each name is to what was typed, so the first matches are the best part of the answer
	/// rather than an accident of where they sit. What the cap left out is described beside them -- how
	/// many of each kind and in each project -- with every group a value <paramref name="kind"/> or
	/// <paramref name="project"/> takes, so the next call can ask for the ones the cap hid.
	/// </para>
	/// </summary>
	/// <param name="snapshot">The solution to search.</param>
	/// <param name="query">The name or abbreviation.</param>
	/// <param name="maxResults">How many matches to list.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	/// <param name="kind">Only matches of this kind, as a match reports it: NamedType, Method, Property, Field or Event.</param>
	/// <param name="project">Only matches declared in this project.</param>
	/// <exception cref="ArgumentException">The solution has no project of that name.</exception>
	public static async Task<SymbolSearchResult> SearchAsync(
		WorkspaceSnapshot snapshot,
		string query,
		int maxResults,
		CancellationToken cancellationToken,
		string? kind = null,
		string? project = null)
	{
		var matches = new List<(ISymbol Symbol, string Project)>();

		// Resolved before the search, so a name no project carries is refused rather than read as a name
		// nothing declares.
		var searched = project is { Length: > 0 }
			? ProjectNames.Resolve(snapshot.Solution, project)
			: snapshot.Solution.Projects;
		var wantedKind = SearchKind(kind);

		foreach (var candidate in searched)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Pattern search understands the abbreviations people actually type -- "SLoader" finds
			// SolutionLoader -- which plain substring matching does not.
			var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(candidate, query, cancellationToken);

			foreach (var symbol in found)
			{
				var isOfKind = wantedKind is null || string.Equals(symbol.Kind.ToString(), wantedKind, StringComparison.OrdinalIgnoreCase);
				if (isOfKind) matches.Add((symbol, candidate.Name));
			}
		}

		var ordered = matches
			.OrderBy(match => match.Symbol.Name.Length)
			.ThenBy(match => match.Symbol.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var truncated = ordered.Length > maxResults;
		var listed = new List<SymbolMatch>();

		// Described only as far as the cap, since a location is a document read and the matches past it
		// are counted, not listed.
		foreach (var (symbol, projectName) in truncated ? ordered[..maxResults] : ordered)
		{
			var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);

			listed.Add(new SymbolMatch
			{
				Name = symbol.Name,
				Kind = symbol.Kind.ToString(),
				Address = SymbolAddress.Of(symbol),
				Signature = symbol.ToDisplayString(SymbolSignature.Format),
				Project = projectName,
				Location = location is null
					? null
					: await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken),
			});
		}

		return new SymbolSearchResult
		{
			Revision = snapshot.Revision,
			Matches = listed,
			TotalCount = ordered.Length,
			Truncated = truncated,
			Shape = truncated ? SearchShape(ordered) : null,
			Notices = truncated
				? [$"Listed the {maxResults} closest of {ordered.Length} matches. Narrow with kind or project -- every group "
					+ $"in the shape is a value one of them takes -- or pass maxResults={ordered.Length} to list them all."]
				: [],
		};
	}

	/// <summary>
	/// The symbol kind a search's <c>kind</c> names, as a match reports it, or null for every kind.
	/// <c>Type</c> is accepted for <c>NamedType</c>, which is what a person calls it. Anything else is
	/// refused, listing what is accepted: a kind no match carries would answer with nothing, which reads
	/// as a name nothing declares. <c>Class</c> or <c>Interface</c> is refused rather than taken as a type,
	/// since the search cannot tell a class from an interface and answering with both would not be the
	/// question asked.
	/// </summary>
	/// <exception cref="ArgumentException">The kind is not one a match carries.</exception>
	private static string? SearchKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
	{
		null or "" => null,
		"namedtype" or "type" => nameof(SymbolKind.NamedType),
		"method" => nameof(SymbolKind.Method),
		"property" => nameof(SymbolKind.Property),
		"field" => nameof(SymbolKind.Field),
		"event" => nameof(SymbolKind.Event),
		_ => throw ArgumentValues.Unknown("kind", kind, "NamedType (or Type)", "Method", "Property", "Field", "Event"),
	};

	/// <summary>How a search's matches divide by kind and by project, each group keyed by the value its argument takes.</summary>
	private static SymbolSearchShape SearchShape(IReadOnlyCollection<(ISymbol Symbol, string Project)> matches) => new()
	{
		Kinds = FacetGroups.Of(matches, match => match.Symbol.Kind.ToString(), (kind, inIt) => new SymbolKindCount { Kind = kind, Count = inIt.Count }),
		Projects = FacetGroups.Of(matches, match => match.Project, (project, inIt) => new SymbolProjectCount { Project = project, Count = inIt.Count }),
	};
}
