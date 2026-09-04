using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoseMcp.Worker;

/// <summary>
/// Finds the one declaration a caller named, or refuses and says what it found instead.
/// <para>
/// Refusing matters more here than anywhere else on a write path. Two overloads, or the two halves
/// of a partial, are the cases where a wrong guess writes perfectly correct code into the wrong
/// member -- the only failure shape with no symptom at all. It compiles, the diff looks like what
/// was asked for, and the behaviour that was meant to change did not.
/// </para>
/// <para>
/// Every refusal names the candidates. A caller that has to go and read the file to find out why
/// its call was rejected has been sent back to the tool this one exists to replace.
/// </para>
/// </summary>
public static class DeclarationLocator
{
	/// <summary>How many candidates an error lists before it starts summarising.</summary>
	private const int Listed = 8;

	/// <summary>
	/// The one declaration of a member, or of a type -- a type declaration is a member too. For
	/// writing: a partial declared in two files is two places a member could go, and which one is
	/// the caller's decision.
	/// </summary>
	public static async Task<DeclarationTarget> FindMemberAsync(
		Solution solution,
		string requested,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var found = await FindAsync(solution, requested, filePath, typesOnly: false, cancellationToken);

		if (found.Declarations.Count == 1) return found.Declarations[0];

		throw found.Declarations.Count == 0 ? found.NotFound() : found.Ambiguous();
	}

	/// <summary>
	/// The one symbol a name refers to, whatever number of places declare it. For reading, where the
	/// several declarations of a partial are all part of the answer rather than a choice to be made
	/// -- and where refusing to describe a partial at all, as writing rightly does, would be absurd.
	/// <para>
	/// Overloads are still ambiguous, because they are genuinely different symbols with different
	/// signatures, and that is what tells the two cases apart.
	/// </para>
	/// </summary>
	public static async Task<DeclarationTarget> FindSymbolAsync(
		Solution solution,
		string requested,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var found = await FindAsync(solution, requested, filePath, typesOnly: false, cancellationToken);

		var bySignature = found.Declarations
			.GroupBy(target => target.Signature, StringComparer.Ordinal)
			.ToArray();

		if (bySignature.Length == 1) return bySignature[0].First();

		throw bySignature.Length == 0 ? found.NotFound() : found.Ambiguous();
	}

	/// <summary>
	/// The declaration of a type, refusing anything else. Separate from <see cref="FindMemberAsync"/>
	/// so that naming a method where a type belongs is answered with what it actually is, rather
	/// than with a puzzling complaint about the code much later on.
	/// </summary>
	public static async Task<TypeTarget> FindTypeAsync(
		Solution solution,
		string requested,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var found = await FindAsync(solution, requested, filePath, typesOnly: true, cancellationToken);

		if (found.Declarations.Count != 1)
		{
			throw found.Declarations.Count == 0 ? found.NotFound() : found.Ambiguous();
		}

		var target = found.Declarations[0];

		// A named type whose declaration is not a type declaration is a delegate, and a delegate has
		// no members to add to.
		if (target.Symbol is not INamedTypeSymbol symbol || target.Declaration is not BaseTypeDeclarationSyntax declaration)
		{
			throw new ArgumentException($"{target.Signature} is a {Kind(target.Symbol)}, which has no members.");
		}

		return new TypeTarget
		{
			Symbol = symbol,
			Document = target.Document,
			Declaration = declaration,
		};
	}

	/// <summary>
	/// Everything the search turned up, and everything needed to explain finding nothing. Kept as a
	/// value rather than resolved here, because what counts as one answer differs between reading
	/// and writing and only the caller knows which it is doing.
	/// </summary>
	private static async Task<Found> FindAsync(
		Solution solution,
		string requested,
		string? filePath,
		bool typesOnly,
		CancellationToken cancellationToken)
	{
		var address = SymbolAddress.Parse(requested);

		var named = (await SymbolFinder.FindSourceDeclarationsAsync(
			solution, address.Name, ignoreCase: false, cancellationToken)).ToArray();

		var matching = named
			.Where(symbol => (!typesOnly || symbol is INamedTypeSymbol) && address.Matches(symbol))
			.ToArray();

		var found = new List<DeclarationTarget>();
		var generated = 0;
		var elsewhere = 0;

		foreach (var symbol in matching)
		{
			foreach (var reference in symbol.DeclaringSyntaxReferences)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var node = await reference.GetSyntaxAsync(cancellationToken);
				if (node.FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } declaration) continue;

				// No document behind the tree means generated code: there is no file to edit, and the
				// generator would produce the same thing again on the next compilation.
				if (solution.GetDocument(reference.SyntaxTree) is not { FilePath.Length: > 0 } document)
				{
					generated++;
					continue;
				}

				if (filePath is not null && !SamePath(document.FilePath!, filePath))
				{
					elsewhere++;
					continue;
				}

				found.Add(new DeclarationTarget
				{
					Symbol = symbol,
					Document = document,
					Declaration = declaration,
				});
			}
		}

		// One file can belong to several projects -- multi-targeting, or a shared project -- and each
		// of them reports the same declaration through a symbol of its own.
		var distinct = found
			.DistinctBy(target => (Path.GetFullPath(target.FilePath).ToUpperInvariant(), target.Declaration.Span))
			.ToArray();

		return new Found
		{
			Address = address,
			Declarations = distinct,
			Named = named,
			Matching = matching,
			Generated = generated,
			Elsewhere = elsewhere,
			FilePath = filePath,
			TypesOnly = typesOnly,
		};
	}

	/// <summary>What the search found, and how to say that it was not enough.</summary>
	private sealed record Found
	{
		public required SymbolAddress Address { get; init; }

		public required IReadOnlyList<DeclarationTarget> Declarations { get; init; }

		public required IReadOnlyList<ISymbol> Named { get; init; }

		public required IReadOnlyList<ISymbol> Matching { get; init; }

		public required int Generated { get; init; }

		public required int Elsewhere { get; init; }

		public required string? FilePath { get; init; }

		public required bool TypesOnly { get; init; }

		public ArgumentException NotFound() =>
			DeclarationLocator.NotFound(Address, Named, Matching, Generated, Elsewhere, FilePath, TypesOnly);

		public ArgumentException Ambiguous() => DeclarationLocator.Ambiguous(Address, Declarations, FilePath);
	}

	/// <summary>
	/// Nothing matched, and the reasons why are worth telling apart: the name exists nowhere, it
	/// exists somewhere other than where the caller said, it exists only in generated code, or it
	/// exists but not in the file the caller pinned it to.
	/// </summary>
	private static ArgumentException NotFound(
		SymbolAddress address,
		IReadOnlyList<ISymbol> named,
		IReadOnlyList<ISymbol> matching,
		int generated,
		int elsewhere,
		string? filePath,
		bool typesOnly)
	{
		if (named.Count == 0)
		{
			return new ArgumentException(
				$"Nothing in the solution is called {Quote(address.Name)}. Ask rose_search_symbols, which matches "
					+ "names by pattern and by abbreviation and returns the qualified name this argument wants.");
		}

		if (matching.Count == 0)
		{
			var qualified = named
				.Where(symbol => !typesOnly || symbol is INamedTypeSymbol)
				.Select(symbol => string.Join(".", SymbolAddress.PathOf(symbol)))
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal)
				.ToArray();

			if (qualified.Length == 0)
			{
				return new ArgumentException(
					$"{Quote(address.Requested)} is a {Kind(named[0])}, not a type. Only a type has members to add to.");
			}

			var overloads = address.Parameters is null
				? string.Empty
				: " No overload takes those parameter types; leave the parameter list off to be told what there is.";

			return new ArgumentException(
				$"Nothing is declared at {Quote(address.Requested)}. {Quote(address.Name)} is declared as "
					+ $"{Summarise(qualified)}.{overloads}");
		}

		if (elsewhere > 0)
		{
			return new ArgumentException(
				$"{Quote(address.Requested)} is declared in this solution, but not in {Path.GetFileName(filePath)}. "
					+ "Leave filePath out and the declaration decides which file it is in.");
		}

		var places = generated > 1 ? $" ({generated} of them)" : string.Empty;

		return new ArgumentException(
			$"{Quote(address.Requested)} is declared in source-generated code{places}, which is not on disk and "
				+ "would be regenerated on the next compilation. Change the generator, or what it reads, instead.");
	}

	private static ArgumentException Ambiguous(
		SymbolAddress address,
		IReadOnlyList<DeclarationTarget> candidates,
		string? filePath)
	{
		var listed = string.Join(
			"; ",
			candidates.Take(Listed).Select(candidate =>
				$"{candidate.Signature} at {Path.GetFileName(candidate.FilePath)}:{LineOf(candidate)}"));

		var more = candidates.Count > Listed ? $" ... and {candidates.Count - Listed} more" : string.Empty;

		var separateSymbols = candidates
			.Select(candidate => candidate.Symbol)
			.Distinct(SymbolEqualityComparer.Default)
			.Count() > 1;

		// One symbol in several places is a partial, which no parameter list can separate however
		// precisely it is written. Several symbols are overloads, which one can.
		var how = !separateSymbols
			? "Pass filePath to say which of its declarations to write to."
			: filePath is null
				? "Name the parameter types to pick one, as Type.Member(int, string), or pass filePath."
				: "Name the parameter types to pick one, as Type.Member(int, string).";

		return new ArgumentException(
			$"{Quote(address.Requested)} matches {candidates.Count} declarations: {listed}{more}. {how}");
	}

	private static int LineOf(DeclarationTarget target) =>
		target.Declaration.SyntaxTree.GetLineSpan(target.Declaration.Span).StartLinePosition.Line + 1;

	private static string Summarise(IReadOnlyList<string> qualified) =>
		string.Join(", ", qualified.Take(Listed))
			+ (qualified.Count > Listed ? $" ... and {qualified.Count - Listed} more" : string.Empty);

	private static string Quote(string? text) => $"'{text}'";

	private static string Kind(ISymbol symbol) => symbol.Kind.ToString().ToLowerInvariant();

	private static bool SamePath(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
