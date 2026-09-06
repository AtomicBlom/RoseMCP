using Microsoft.CodeAnalysis;

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
/// runs only after the declaration search has found nothing at all.
/// </para>
/// </summary>
public static class MetadataSymbols
{
	/// <summary>
	/// How many generic arities a name is tried at. An address drops type arguments, so List and
	/// List&lt;int&gt; arrive here identically and metadata spells the type List`1.
	/// </summary>
	private const int Arities = 8;

	/// <summary>
	/// The one symbol in metadata this address names, or null when nothing there carries the name.
	/// </summary>
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

			foreach (var symbol in Candidates(compilation, address))
			{
				// Keyed rather than compared, because the same metadata symbol reached through two
				// projects is two ISymbol instances and SymbolEqualityComparer says so.
				found.TryAdd(Identity(symbol), symbol);
			}
		}

		if (found.Count == 0) return null;
		if (found.Count == 1) return found.Values.First();

		throw new ArgumentException(
			$"{Quote(address.Requested)} names {found.Count} different symbols in the assemblies this solution "
				+ $"references: {string.Join(", ", found.Keys.Order(StringComparer.Ordinal))}. "
				+ "Qualify it further to say which.");
	}

	/// <summary>
	/// What the address could be naming: the whole path as a type, or its last segment as a member of
	/// the type the rest names. A constructor address already spells its type, so it is only the
	/// first.
	/// </summary>
	private static IEnumerable<ISymbol> Candidates(Compilation compilation, SymbolAddress address)
	{
		foreach (var type in TypesNamed(compilation, address.Path))
		{
			if (address.Constructor != ConstructorKind.None)
			{
				foreach (var constructor in type.GetMembers().Where(address.Matches)) yield return constructor;
				continue;
			}

			if (address.Matches(type)) yield return type;
		}

		if (address.Constructor != ConstructorKind.None || address.Path.Count < 2) yield break;

		foreach (var type in TypesNamed(compilation, [.. address.Path.Take(address.Path.Count - 1)]))
		{
			foreach (var member in type.GetMembers(address.Name).Where(address.Matches)) yield return member;
		}
	}

	/// <summary>
	/// The types a dotted path could name, asked for the ways metadata spells one rather than the way
	/// a caller writes it: an arity suffix the address has dropped, and a '+' where a nested type is
	/// written with a '.'.
	/// </summary>
	private static IEnumerable<INamedTypeSymbol> TypesNamed(Compilation compilation, IReadOnlyList<string> path)
	{
		if (path.Count == 0) yield break;

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

	/// <summary>Assembly and full name together, which is what makes two candidates genuinely two.</summary>
	private static string Identity(ISymbol symbol) =>
		$"{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"
			+ $" in {symbol.ContainingAssembly?.Identity.Name ?? "an unnamed assembly"}";

	private static string Quote(string text) => $"'{text}'";
}
