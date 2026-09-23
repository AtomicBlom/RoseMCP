using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoseMcp.Worker;

/// <summary>
/// Finds a symbol in metadata, for the reads that can answer about one.
/// <para>
/// Every type in every referenced assembly is already in the compilations this worker holds, so
/// refusing to say what Microsoft.CodeAnalysis.WorkspaceDiagnostic is because nothing in the
/// solution declares it answers a narrower question than the one asked -- and sends the caller to
/// a decompiler or a web search for something the compilation had to hand.
/// </para>
/// <para>
/// What it must not do is choose. A name that resolves to different types in different projects is
/// reported with both, never picked between, for the same reason rose_resolve_name never returns a
/// first candidate: the wrong one is a complete, well-formed answer about something else. Source
/// wins outright, because a caller naming a type this solution declares means that one, and this
/// runs only after the declaration search has found nothing matching.
/// </para>
/// </summary>
public static class MetadataSymbols
{
	/// <summary>How many candidates a refusal lists before it starts summarising.</summary>
	private const int Listed = 8;

	/// <summary>
	/// How many generic arities a name is tried at. An address drops type arguments, so List and
	/// List&lt;int&gt; arrive here identically and metadata spells the type List`1.
	/// </summary>
	private const int Arities = 8;

	/// <summary>
	/// The one symbol in metadata this address names, or null when nothing there carries the name.
	/// </summary>
	/// <exception cref="ArgumentException">Several symbols carry it, and choosing is not this to do.</exception>
	public static async Task<ISymbol?> FindAsync(
		Solution solution,
		SymbolAddress address,
		CancellationToken cancellationToken)
	{
		var found = new Dictionary<string, ISymbol>(StringComparer.Ordinal);

		foreach (var project in solution.Projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null) continue;

			foreach (var symbol in await CandidatesAsync(project, compilation, address, cancellationToken))
			{
				// Keyed rather than compared, because the same metadata symbol reached through two
				// projects is two ISymbol instances and SymbolEqualityComparer says so.
				found.TryAdd(Identity(symbol), symbol);
			}
		}

		if (found.Count == 0) return null;
		if (found.Count == 1) return found.Values.First();

		throw Ambiguous(address, found);
	}

	/// <summary>
	/// What the address could be naming: the whole path as a type, or its last segment as a member of
	/// the type the rest names. A constructor address already spells its type, so it is only the
	/// first.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> CandidatesAsync(
		Project project,
		Compilation compilation,
		SymbolAddress address,
		CancellationToken cancellationToken)
	{
		var candidates = new List<ISymbol>();

		foreach (var type in await TypesNamedAsync(project, compilation, address.Path, cancellationToken))
		{
			if (address.Constructor != ConstructorKind.None)
			{
				candidates.AddRange(type.GetMembers().Where(address.Matches));
				continue;
			}

			if (address.Matches(type)) candidates.Add(type);
		}

		if (address.Constructor != ConstructorKind.None || address.Path.Count < 2) return candidates;

		var containing = await TypesNamedAsync(
			project, compilation, [.. address.Path.Take(address.Path.Count - 1)], cancellationToken);

		foreach (var type in containing)
		{
			candidates.AddRange(type.GetMembers(address.Name).Where(address.Matches));
		}

		return candidates;
	}

	/// <summary>
	/// The types a dotted path could name, asked for the ways metadata spells one rather than the way
	/// a caller writes it: an arity suffix the address has dropped, and a '+' where a nested type is
	/// written with a '.'.
	/// <para>
	/// A lone segment is a name rather than a path, and no metadata name lookup will ever find one --
	/// the compilation spells StringBuilder as System.Text.StringBuilder and by nothing else. So that
	/// case goes to the declaration index instead, which is what lets a caller name a library type
	/// the way the code in front of them writes it rather than having to know its namespace first.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<INamedTypeSymbol>> TypesNamedAsync(
		Project project,
		Compilation compilation,
		IReadOnlyList<string> path,
		CancellationToken cancellationToken)
	{
		if (path.Count == 0) return [];

		var named = ByMetadataName(compilation, path).ToArray();

		if (path.Count > 1 || named.Length > 0) return named;

		var declared = await SymbolFinder.FindDeclarationsAsync(
			project, path[0], ignoreCase: false, SymbolFilter.Type, cancellationToken);

		return
		[
			.. declared
				.OfType<INamedTypeSymbol>()
				.Where(type => !type.Locations.Any(location => location.IsInSource))

				// Asked of the compilation rather than of DeclaredAccessibility, so an internal type
				// reached through InternalsVisibleTo counts and one that is merely internal does not.
				// A caller writing a bare name means one the code in front of them could have written.
				.Where(type => compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)),
		];
	}

	/// <summary>The types a path names when it is spelled the way metadata spells one.</summary>
	private static IEnumerable<INamedTypeSymbol> ByMetadataName(Compilation compilation, IReadOnlyList<string> path)
	{
		var dotted = string.Join('.', path);
		var nested = path.Count >= 2
			? string.Join('.', path.Take(path.Count - 1)) + "+" + path[^1]
			: null;

		for (var arity = 0; arity <= Arities; arity++)
		{
			var suffix = arity == 0 ? string.Empty : $"`{arity}";

			foreach (var type in compilation.GetTypesByMetadataName(dotted + suffix)) yield return type;

			if (nested is null) continue;

			foreach (var type in compilation.GetTypesByMetadataName(nested + suffix)) yield return type;
		}
	}

	/// <summary>
	/// Assembly and address together, which is what makes two candidates genuinely two.
	/// <para>
	/// The address rather than the name, because every overload of a method shares a name, a
	/// containing type and an assembly: keyed on those, eight <c>File.WriteAllTextAsync</c> collapse
	/// into one candidate, the refusal below never fires, and the caller is answered about whichever
	/// overload the enumeration reached first. That failure has no symptom -- no references found for
	/// an overload nobody asked about is a well-formed answer, and nothing in it says the question was
	/// ambiguous. What separates overloads is their parameters, so the key has to carry them.
	/// </para>
	/// <para>
	/// An address rather than a signature because the refusal tells the caller to qualify further, and
	/// what it lists is what they will write back: <see cref="SymbolSignature.Format"/> leads with the
	/// return type and names the parameters, and none of that parses. Nothing reaching here is a local
	/// or a parameter, so the fallback is for totality rather than for a case that arises.
	/// </para>
	/// </summary>
	private static string Identity(ISymbol symbol) =>
		$"{SymbolAddress.Of(symbol) ?? SymbolSignature.Of(symbol)}"
			+ $" in {symbol.ContainingAssembly?.Identity.Name ?? "an unnamed assembly"}";

	/// <summary>
	/// Several symbols carry the name, and picking one is the thing this must never do. The refusal
	/// names them and says how to separate them: a caller told only that its name was ambiguous has
	/// to go and find the candidates somewhere else, which is the decompiler this exists to save it
	/// from. What it lists is addresses, so the way out is to paste one back.
	/// </summary>
	private static ArgumentException Ambiguous(SymbolAddress address, Dictionary<string, ISymbol> found)
	{
		var identities = found.Keys.Order(StringComparer.Ordinal).ToArray();

		var listed = string.Join("; ", identities.Take(Listed))
			+ (identities.Length > Listed ? $" ... and {identities.Length - Listed} more" : string.Empty);

		// Overloads are one member written several ways, and a parameter list separates them exactly.
		// Anything else is several different members, and only more of the name will do it.
		var name = found.Values.First().Name;
		var areOverloads = found.Values.All(symbol =>
			symbol is IMethodSymbol or IPropertySymbol
				&& string.Equals(symbol.Name, name, StringComparison.Ordinal));

		var how = areOverloads
			? "Qualify it further. Name the parameter types to pick one, as Type.Member(int, string)."
			: "Qualify it further to say which.";

		return new ArgumentException(
			$"{Quote(address.Requested)} names {found.Count} different symbols in the assemblies this solution "
				+ $"references: {listed}. {how}");
	}

	private static string Quote(string text) => $"'{text}'";
}
