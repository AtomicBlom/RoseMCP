using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoseMcp.Worker;

/// <summary>
/// The one way a name becomes symbols: every tool that takes a symbol by name, read or write, asks
/// this, so an address that resolves for one tool resolves for all of them.
/// <para>
/// Asked of each project's compilation rather than of the solution's declaration index. The index
/// lists what the syntax declares under a name, and a property a record synthesises from a positional
/// parameter is declared by no syntax of that name, so a resolver built on it cannot reach
/// <c>SymbolLocation.TypeName</c> at all. The compilation lists a type's members as the language has
/// them, positional properties included, so the type path is resolved and its members are asked for
/// by name. Two resolvers would disagree about some address, and which tool a caller met first would
/// decide what it learned about the grammar.
/// </para>
/// <para>
/// Source wins outright. Referenced assemblies are searched only when nothing in source is at the
/// address under any reading -- a source type a constructor reading reaches counts, whether or not it
/// declares the constructor -- and only for a caller that can say something true about a symbol with
/// no file: carrying the address's last segment somewhere else in the solution is not being at the
/// address, and a library member is reached whatever the solution happens to declare under its leaf
/// name. A bare name is still answered from source whenever source carries it, which keeps it
/// ambiguous, and refused as such, wherever it is ambiguous.
/// </para>
/// </summary>
public static class SymbolResolver
{
	/// <summary>Every symbol the address reaches, and what is needed to say why none did.</summary>
	/// <param name="solution">The solution to resolve against.</param>
	/// <param name="address">The address, parsed.</param>
	/// <param name="includeMetadata">
	/// Whether an address source does not reach may be answered from a referenced assembly. Only a read
	/// can say anything true about a symbol with no source, and a caller that pinned a file has said
	/// the answer is in this solution's source.
	/// </param>
	/// <param name="cancellationToken">Cancels the compilations the search builds.</param>
	/// <exception cref="ArgumentException">
	/// Source reaches nothing and several symbols in referenced assemblies are at the address.
	/// </exception>
	public static async Task<SymbolResolution> ResolveAsync(
		Solution solution,
		SymbolAddress address,
		bool includeMetadata,
		CancellationToken cancellationToken)
	{
		var reached = new List<ISymbol>();
		var named = new List<ISymbol>();
		var constructed = new List<INamedTypeSymbol>();

		foreach (var project in solution.Projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null) continue;

			named.AddRange(Called(compilation, address.Name));

			foreach (var reading in address.Readings)
			{
				if (reading.Constructor == ConstructorKind.None)
				{
					reached.AddRange(At(compilation, reading, cancellationToken));
					continue;
				}

				var types = TypesAt(compilation, reading.Path, reading.Anchored);

				constructed.AddRange(types);
				reached.AddRange(types.SelectMany(type => Declared(type.Constructors)).Where(reading.Matches));
			}
		}

		var source = reached.Distinct(SymbolEqualityComparer.Default).ToArray();

		// A constructor address that reached a source type is about that type, whatever constructors it
		// declares: a library type of the same name is somebody else's class.
		var reachedSource = source.Length > 0 || constructed.Count > 0;

		if (reachedSource || !includeMetadata)
		{
			return new SymbolResolution
			{
				Address = address,
				Source = source,
				Named = named,
				Constructed = constructed,
			};
		}

		return new SymbolResolution
		{
			Address = address,
			Source = [],
			Named = named,
			Constructed = constructed,
			Metadata = await MetadataSymbols.FindAsync(solution, address, cancellationToken),
			MetadataSearched = true,
		};
	}

	/// <summary>
	/// Types of that name a project can see: in its own source, in the projects it references, and in
	/// every assembly it references. For working out which namespace would bring a name into scope.
	/// </summary>
	public static async Task<IEnumerable<ISymbol>> TypesCalledAsync(
		Project project,
		string name,
		CancellationToken cancellationToken) =>
		await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, SymbolFilter.Type, cancellationToken);

	/// <summary>Members of that name a project can see, wherever they are declared.</summary>
	public static async Task<IEnumerable<ISymbol>> MembersCalledAsync(
		Project project,
		string name,
		CancellationToken cancellationToken) =>
		await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, SymbolFilter.Member, cancellationToken);

	/// <summary>Types of that name anywhere in the solution's own source, whoever can see them.</summary>
	public static async Task<IEnumerable<ISymbol>> SourceTypesCalledAsync(
		Solution solution,
		string name,
		CancellationToken cancellationToken) =>
		await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, SymbolFilter.Type, cancellationToken);

	/// <summary>
	/// How many leading segments of a dotted name are a namespace this compilation can see, counting
	/// from the global namespace: two for <c>System.Text.Encoding</c>, none for <c>Encoding.UTF8</c>.
	/// </summary>
	public static int NamespaceDepth(Compilation compilation, IReadOnlyList<string> segments) =>
		Depth(compilation.GlobalNamespace, segments);

	/// <summary>
	/// How many leading segments of a dotted name are a namespace this compilation's own source
	/// declares something in, which is what says the project is where the namespace comes from rather
	/// than one more project that can see it.
	/// </summary>
	public static int DeclaredNamespaceDepth(Compilation compilation, IReadOnlyList<string> segments) =>
		Depth(compilation.Assembly.GlobalNamespace, segments);

	private static int Depth(INamespaceSymbol root, IReadOnlyList<string> segments)
	{
		var current = root;
		var depth = 0;

		foreach (var segment in segments)
		{
			if (current.GetMembers(segment).OfType<INamespaceSymbol>().FirstOrDefault() is not { } next) break;

			current = next;
			depth++;
		}

		return depth;
	}

	/// <summary>
	/// The types of that name declared directly in the namespace <paramref name="space"/>, as this
	/// compilation sees it: its own source, the projects it references and every assembly it does.
	/// </summary>
	public static IReadOnlyList<INamedTypeSymbol> TypesIn(Compilation compilation, IReadOnlyList<string> space, string name)
	{
		var current = compilation.GlobalNamespace;

		foreach (var segment in space)
		{
			if (current.GetMembers(segment).OfType<INamespaceSymbol>().FirstOrDefault() is not { } next) return [];

			current = next;
		}

		return current.GetTypeMembers(name);
	}

	/// <summary>
	/// What one reading reaches in one compilation's source: anything declared under its name that
	/// the reading matches, and the members of that name of the types its path leads to.
	/// <para>
	/// The members are asked of the type rather than found by name because that is what reaches a
	/// positional record property, which no declaration of its name exists to find. A bare name has no
	/// type path to follow, so records are asked directly: they are the only types whose members a
	/// parameter declares.
	/// </para>
	/// </summary>
	private static IEnumerable<ISymbol> At(Compilation compilation, SymbolAddress reading, CancellationToken cancellationToken)
	{
		foreach (var symbol in Called(compilation, reading.Name).Where(reading.Matches)) yield return symbol;

		var containers = reading.Path.Count >= 2
			? TypesAt(compilation, [.. reading.Path.Take(reading.Path.Count - 1)], reading.Anchored)
			: Records(compilation, cancellationToken);

		foreach (var container in containers)
		{
			foreach (var member in Declared(container.GetMembers(reading.Name)).Where(reading.Matches))
			{
				yield return member;
			}
		}
	}

	/// <summary>The source types whose own path ends with <paramref name="path"/>.</summary>
	private static IReadOnlyList<INamedTypeSymbol> TypesAt(
		Compilation compilation,
		IReadOnlyList<string> path,
		bool anchored) =>
		[
			.. compilation.GetSymbolsWithName(path[^1], SymbolFilter.Type)
				.OfType<INamedTypeSymbol>()
				.Where(type => SymbolAddress.IsAt(type, path, anchored)),
		];

	/// <summary>
	/// The source records that declare members through a parameter list, which are the members a
	/// search by name cannot reach.
	/// </summary>
	private static IEnumerable<INamedTypeSymbol> Records(Compilation compilation, CancellationToken cancellationToken) =>
		compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, cancellationToken)
			.OfType<INamedTypeSymbol>()
			.Where(type => type.IsRecord && type.DeclaringSyntaxReferences.Any(reference =>
				reference.GetSyntax(cancellationToken) is RecordDeclarationSyntax { ParameterList: not null }));

	/// <summary>
	/// Source symbols of that name, as declared. Implicit ones are dropped because nothing here can be
	/// written over: a record's synthesised equality, a default constructor.
	/// </summary>
	private static IEnumerable<ISymbol> Called(Compilation compilation, string name) =>
		Declared(compilation.GetSymbolsWithName(name, SymbolFilter.TypeAndMember));

	private static IEnumerable<T> Declared<T>(IEnumerable<T> symbols)
		where T : ISymbol =>
		symbols.Where(symbol => !symbol.IsImplicitlyDeclared && symbol.DeclaringSyntaxReferences.Length > 0);
}
