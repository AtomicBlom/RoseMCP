using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Works out which namespace an unresolved name needs, rather than being told.
/// <para>
/// The import tools take the namespace from the caller, which is the common case by a distance --
/// someone writing <c>Encoding.UTF8</c> knows it is <c>System.Text</c>. This is the other half, and
/// it is a search with a refusal condition rather than an edit: the compilation already holds every
/// type in every referenced assembly, so the question "what would make this name resolve" is one
/// lookup against something that has just been built anyway.
/// </para>
/// <para>
/// The IDE's own add-import fix is not a route to this. It lives in
/// <c>Microsoft.CodeAnalysis.CSharp.Features</c>, which is the IDE layer and is deliberately not
/// referenced here, so <c>rose_apply_code_fix</c> on a CS0246 finds nothing however many fixers a
/// solution's analyzers ship.
/// </para>
/// <para>
/// What it must not do is pick. Plenty of names live in two or three namespaces at once, and the
/// wrong import is the worst kind of wrong here: it compiles, and binds to the wrong type. Two
/// candidates are reported as two candidates.
/// </para>
/// </summary>
public static class NameResolver
{
	public static async Task<NameResolutionResult> ResolveAsync(
		WorkspaceSnapshot snapshot,
		ResolveNameRequest request,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var (name, arity) = Parse(request.Name, request.Arity);

		if (name.Length == 0) throw new ArgumentException("Name something to resolve.");

		var document = request.FilePath is { Length: > 0 } path
			? SymbolLocator.FindDocument(snapshot.Solution, path)
				?? throw new ArgumentException($"No document in the solution matches '{path}'.")
			: null;

		// The file's own project, when there is one. A namespace only helps if the project already
		// references the assembly holding it, so answering out of a project that does -- when the
		// caller's does not -- is an answer that will not compile.
		IReadOnlyList<Project> projects = document is null ? [.. snapshot.Solution.Projects] : [document.Project];

		var notices = new List<string>(snapshot.Notices);

		progress?.Report($"Looking for a type called {name}", 20);

		var found = await TypesAsync(projects, name, cancellationToken);

		// Only once nothing of that name is a type. A member search matches every method of that
		// name in every referenced assembly -- thousands of them, for a name like Count -- and it
		// answers a question the type search has already ruled out.
		if (found.Count == 0)
		{
			progress?.Report($"Nothing is called {name}; looking for an extension method", 50);

			found = await ExtensionsAsync(projects, name, cancellationToken);

			if (found.Count > 0)
			{
				notices.Add(
					$"No type is called {name}; these are extension methods. The namespace still has to be "
						+ "imported for the method to be found, even though the name itself is not in it.");
			}
		}

		// Still nothing the file can reach, so the useful answer is which of the two things went
		// wrong: it is not written yet, or it is written somewhere this project cannot see.
		var unreferenced = found.Count == 0 && document is not null;

		if (unreferenced)
		{
			progress?.Report($"Looking for {name} elsewhere in the solution", 70);

			found = await ElsewhereAsync(snapshot.Solution, document!.Project, name, cancellationToken);
		}

		progress?.Report("Working out what is in scope already", 85);

		var inScope = await ScopeAsync(document, cancellationToken);
		var described = found.Select(symbol => Describe(symbol, name, arity, unreferenced, document, inScope));
		var ordered = Ordered(described);

		// By namespace rather than by candidate: two overloads of one extension method are one import
		// and one answer, whereas two namespaces are a choice only the caller can make.
		var spaces = ordered
			.Where(Usable)
			.Select(candidate => candidate.Namespace)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		var maxResults = request.MaxResults <= 0 ? 20 : request.MaxResults;
		var truncated = ordered.Count > maxResults;

		notices.AddRange(Notices(name, ordered, spaces, document, unreferenced));

		return new NameResolutionResult
		{
			Revision = snapshot.Revision,
			Name = name,
			Candidates = truncated ? [.. ordered.Take(maxResults)] : ordered,
			Import = spaces.Length == 1 ? spaces[0] : null,
			TotalCount = ordered.Count,
			Truncated = truncated,
			Notices = notices,
		};
	}

	/// <summary>
	/// Types of that name in source, in referenced projects, and in every referenced assembly --
	/// which is the universe the file can actually reach.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> TypesAsync(
		IReadOnlyList<Project> projects,
		string name,
		CancellationToken cancellationToken)
	{
		return await GatherAsync(
			projects,
			project => SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, SymbolFilter.Type, cancellationToken),
			symbol => symbol is INamedTypeSymbol,
			cancellationToken);
	}

	/// <summary>
	/// Extension methods of that name, which are the third way a name fails to resolve: the method
	/// is not on the type and the namespace holding it is not imported.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> ExtensionsAsync(
		IReadOnlyList<Project> projects,
		string name,
		CancellationToken cancellationToken)
	{
		return await GatherAsync(
			projects,
			project => SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, SymbolFilter.Member, cancellationToken),
			symbol => symbol is IMethodSymbol { IsExtensionMethod: true },
			cancellationToken);
	}

	/// <summary>
	/// Types of that name anywhere in the solution's own source, used only once the file's project
	/// has been shown not to reach any. Source only: a metadata type the project cannot see is one
	/// it was never going to be able to use, whereas a project in the same solution is a reference
	/// away.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> ElsewhereAsync(
		Solution solution,
		Project asking,
		string name,
		CancellationToken cancellationToken)
	{
		var found = await SymbolFinder.FindSourceDeclarationsAsync(
			solution, name, ignoreCase: false, SymbolFilter.Type, cancellationToken);

		return [.. found.OfType<INamedTypeSymbol>().Where(symbol => symbol.ContainingAssembly?.Name != asking.AssemblyName)];
	}

	/// <summary>
	/// Runs one search over every project and keeps what survives the filter, accessibility, and
	/// having been seen already.
	/// <para>
	/// Deduplicated by full signature because the interesting types are in metadata, and every
	/// project in the solution references the same <c>System.Runtime</c>: without this, a seventeen
	/// project solution answers <c>Encoding</c> seventeen times.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> GatherAsync(
		IReadOnlyList<Project> projects,
		Func<Project, Task<IEnumerable<ISymbol>>> search,
		Func<ISymbol, bool> wanted,
		CancellationToken cancellationToken)
	{
		var kept = new List<ISymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var project in projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null) continue;

			foreach (var symbol in await search(project))
			{
				if (!wanted(symbol) || symbol.IsImplicitlyDeclared) continue;

				// Asked of the compilation rather than of DeclaredAccessibility, so an internal type
				// reached through InternalsVisibleTo counts and one that is merely internal does not.
				if (!compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly)) continue;

				if (seen.Add(SymbolSignature.Of(symbol))) kept.Add(symbol);
			}
		}

		return kept;
	}

	/// <summary>
	/// One candidate: the namespace that would bring it into scope, and every reason that might not
	/// be enough.
	/// </summary>
	private static NameCandidate Describe(
		ISymbol symbol,
		string name,
		int? arity,
		bool unreferenced,
		Document? document,
		Func<string, string?> inScope)
	{
		// For a nested type it is the outermost container that the namespace holds, so that is what
		// has to be imported -- and importing it still will not let the name be written bare.
		var outermost = Outermost(symbol);
		var space = outermost.ContainingNamespace;
		var qualified = space is null || space.IsGlobalNamespace ? string.Empty : space.ToDisplayString();

		return new NameCandidate
		{
			Namespace = qualified,
			Symbol = SymbolSignature.Of(symbol),
			Kind = KindOf(symbol),
			Assembly = symbol.ContainingAssembly?.Name ?? "(unknown)",
			Arity = symbol is INamedTypeSymbol type ? type.Arity : 0,
			InSource = symbol.Locations.Any(location => location.IsInSource),
			AlreadyInScope = qualified.Length == 0
				? "declared in the global namespace, which needs no import"
				: inScope(qualified),
			Caveat = Caveat(symbol, name, arity, unreferenced, document),
		};
	}

	/// <summary>
	/// Why importing the namespace would not be the whole fix, or null when it would.
	/// <para>
	/// Each of these is a name that looks resolvable and is not, which is exactly the case where
	/// adding the obvious using directive produces a second error rather than none.
	/// </para>
	/// </summary>
	private static string? Caveat(ISymbol symbol, string name, int? arity, bool unreferenced, Document? document)
	{
		if (unreferenced && document is not null)
		{
			var project = symbol.ContainingAssembly?.Name ?? "another project";

			return $"declared in {project}, which {document.Project.Name} does not reference -- add the project "
				+ "reference first, or the import will not resolve";
		}

		// Only asked of types. An extension method is always inside a static class, and that is how it
		// is meant to be reached -- naming its container as an obstacle would make every one of them
		// look unusable.
		if (symbol is not INamedTypeSymbol type) return null;

		if (type.ContainingType is not null)
		{
			return $"nested in {type.ContainingType.Name}, so importing the namespace is not enough -- "
				+ $"write {Through(type)} rather than {name}";
		}

		if (arity is { } wanted && type.Arity != wanted)
		{
			return $"takes {type.Arity} type argument(s), and the name was used with {wanted}";
		}

		return null;
	}

	/// <summary>Usable first, then a stable order, so a caller reading only the first line gets the answer.</summary>
	private static IReadOnlyList<NameCandidate> Ordered(IEnumerable<NameCandidate> candidates) =>
		[
			.. candidates
				.OrderByDescending(Usable)
				.ThenBy(candidate => candidate.Namespace, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.Symbol, StringComparer.Ordinal),
		];

	private static bool Usable(NameCandidate candidate) =>
		candidate.AlreadyInScope is null && candidate.Caveat is null;

	private static IEnumerable<string> Notices(
		string name,
		IReadOnlyList<NameCandidate> candidates,
		IReadOnlyList<string> spaces,
		Document? document,
		bool unreferenced)
	{
		var where = document is null ? "this solution" : document.Project.Name;

		if (candidates.Count == 0)
		{
			yield return $"Nothing called {name} is reachable from {where}, and nothing in the solution's own "
				+ "source is called that either, so it is not written yet.";

			yield break;
		}

		if (unreferenced) yield break;

		if (spaces.Count > 1)
		{
			yield return $"{spaces.Count} namespaces would resolve {name}: {string.Join(", ", spaces)}. Pick one "
				+ "rather than taking the first -- the wrong import compiles and binds to the wrong type, which is "
				+ "the one failure here with no symptom.";

			yield break;
		}

		if (spaces.Count == 1) yield break;

		if (candidates.All(candidate => candidate.AlreadyInScope is not null))
		{
			yield return $"{name} is in scope already, so the error is something else: a misspelling, an "
				+ "accessibility problem, or the wrong number of type arguments.";
		}
	}

	/// <summary>
	/// The name written through its containers, which is what a nested type has to be spelled as
	/// once its namespace is imported.
	/// </summary>
	private static string Through(ISymbol symbol)
	{
		var parts = new List<string> { symbol.Name };

		for (var container = symbol.ContainingType; container is not null; container = container.ContainingType)
		{
			parts.Insert(0, container.Name);
		}

		return string.Join(".", parts);
	}

	/// <summary>The outermost type, which is the one the namespace actually contains.</summary>
	private static ISymbol Outermost(ISymbol symbol)
	{
		var current = symbol;

		while (current.ContainingType is { } container) current = container;

		return current;
	}

	private static string KindOf(ISymbol symbol) => symbol switch
	{
		IMethodSymbol => "ExtensionMethod",
		INamedTypeSymbol type => type.TypeKind.ToString(),
		_ => symbol.Kind.ToString(),
	};

	/// <summary>
	/// What is already in scope in the file, asked once and reused. Without a file there is nothing
	/// to ask: a namespace's being imported is a property of a file, not of a solution.
	/// </summary>
	private static async Task<Func<string, string?>> ScopeAsync(Document? document, CancellationToken cancellationToken)
	{
		if (document is null) return _ => null;

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var model = await document.GetSemanticModelAsync(cancellationToken);

		if (root is not CompilationUnitSyntax unit || model is null) return _ => null;

		return space => UsingDirectives.AlreadyInScope(unit, model, space, cancellationToken);
	}

	/// <summary>
	/// The name to search for, taken out of however the code spells it.
	/// <para>
	/// The first segment of a dotted name, because that is the part that has to resolve:
	/// <c>Encoding.UTF8</c> fails on <c>Encoding</c>. Type arguments then come off and are counted,
	/// because a name used at one arity is not resolved by a type of another, and the use site is
	/// the only place that number appears.
	/// </para>
	/// <para>
	/// Public so it can be tested without a solution. Everything else here needs a compilation, and
	/// this is the part with rules of its own rather than Roslyn's.
	/// </para>
	/// </summary>
	public static (string Name, int? Arity) Parse(string supplied, int? arity = null)
	{
		var name = supplied.Trim();
		var dot = TopLevelDot(name);

		if (dot >= 0) name = name[..dot];

		var angle = name.IndexOf('<', StringComparison.Ordinal);

		if (angle < 0) return (name, arity);

		var close = name.LastIndexOf('>');

		if (close > angle) arity ??= Counted(name[(angle + 1)..close]);

		return (name[..angle], arity);
	}

	/// <summary>
	/// Where the first dot outside any type argument list is, or -1.
	/// <para>
	/// Depth matters because the dot in <c>List&lt;Foo.Bar&gt;</c> qualifies an argument rather than
	/// the name in hand: splitting on it would search for <c>List&lt;Foo</c>, which nothing is called
	/// and which would be reported as not written yet, as confidently as any other answer.
	/// </para>
	/// </summary>
	private static int TopLevelDot(string name)
	{
		var depth = 0;

		for (var index = 0; index < name.Length; index++)
		{
			if (name[index] == '<') depth++;
			else if (name[index] == '>') depth--;
			else if (name[index] == '.' && depth == 0) return index;
		}

		return -1;
	}

	/// <summary>
	/// How many type arguments a list holds, counting only the commas at its own depth so
	/// <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c> counts two rather than three.
	/// <para>
	/// Commas plus one, with no special case for an empty list: <c>List&lt;&gt;</c> is the unbound
	/// form of a type taking one argument, not of a type taking none.
	/// </para>
	/// </summary>
	private static int Counted(string arguments)
	{
		var depth = 0;
		var count = 1;

		foreach (var character in arguments)
		{
			if (character == '<') depth++;
			else if (character == '>') depth--;
			else if (character == ',' && depth == 0) count++;
		}

		return count;
	}
}
