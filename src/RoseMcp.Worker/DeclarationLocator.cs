using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
/// <para>
/// What the name reaches is <see cref="SymbolResolver"/>'s answer, the same one every tool gets;
/// this turns it into declarations and decides what counts as one.
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
		var resolution = await SourceAsync(solution, requested, cancellationToken);
		var found = await FindAsync(solution, resolution, filePath, typesOnly: false, cancellationToken);

		if (found.Declarations.Count != 1)
		{
			throw found.Declarations.Count == 0 ? found.NotFound() : found.Ambiguous();
		}

		var target = found.Declarations[0];

		if (target.DeclaredByParameter) throw Positional(target);

		return target;
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
		CancellationToken cancellationToken) =>
		await FindSymbolAsync(
			solution, await SourceAsync(solution, requested, cancellationToken), filePath, cancellationToken);

	/// <summary>
	/// The one symbol a resolution reached in source, for a caller that resolved the name itself
	/// because it may answer from metadata when source has nothing.
	/// </summary>
	public static async Task<DeclarationTarget> FindSymbolAsync(
		Solution solution,
		SymbolResolution resolution,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var found = await FindAsync(solution, resolution, filePath, typesOnly: false, cancellationToken);

		var bySignature = found.Declarations
			.GroupBy(target => target.Signature, StringComparer.Ordinal)
			.ToArray();

		if (bySignature.Length == 1) return bySignature[0].First();

		throw bySignature.Length == 0 ? found.NotFound() : found.Ambiguous();
	}

	/// <summary>
	/// The declaration of a type, refusing anything else. Separate from <see cref="FindMemberAsync"/>
	/// so that naming a method where a type belongs is answered with what it actually is, rather
	/// than with a puzzling complaint about the code much later on. A repeated last segment is read
	/// only as a type here, since a constructor is never one.
	/// </summary>
	public static async Task<TypeTarget> FindTypeAsync(
		Solution solution,
		string requested,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var resolution = await SourceAsync(solution, requested, cancellationToken);
		var found = await FindAsync(solution, resolution, filePath, typesOnly: true, cancellationToken);

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

	private static Task<SymbolResolution> SourceAsync(Solution solution, string requested, CancellationToken cancellationToken) =>
		SymbolResolver.ResolveAsync(solution, SymbolAddress.Parse(requested), includeMetadata: false, cancellationToken);

	/// <summary>
	/// The declarations behind what the resolution reached, and everything needed to explain finding
	/// none. Kept as a value rather than resolved here, because what counts as one answer differs
	/// between reading and writing and only the caller knows which it is doing.
	/// </summary>
	private static async Task<Found> FindAsync(
		Solution solution,
		SymbolResolution resolution,
		string? filePath,
		bool typesOnly,
		CancellationToken cancellationToken)
	{
		var matching = typesOnly
			? [.. resolution.Source.Where(symbol => symbol is INamedTypeSymbol)]
			: resolution.Source;

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
					DeclaredByParameter = node is ParameterSyntax,
				});
			}
		}

		// One file can belong to several projects -- multi-targeting, or a shared project -- and each
		// of them reports the same declaration through a symbol of its own. The span alone is not the
		// declaration: a record and the positional property one of its parameters declares share the
		// record's span, and are a type and its member.
		var distinct = found
			.DistinctBy(target => (
				Path.GetFullPath(target.FilePath).ToUpperInvariant(),
				target.Declaration.Span,
				SymbolAddress.Of(target.Symbol) ?? target.Signature))
			.ToArray();

		return new Found
		{
			Resolution = resolution,
			Declarations = distinct,
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
		public required SymbolResolution Resolution { get; init; }

		public required IReadOnlyList<DeclarationTarget> Declarations { get; init; }

		public required IReadOnlyList<ISymbol> Matching { get; init; }

		public required int Generated { get; init; }

		public required int Elsewhere { get; init; }

		public required string? FilePath { get; init; }

		public required bool TypesOnly { get; init; }

		public ArgumentException NotFound() =>
			DeclarationLocator.NotFound(Resolution, Matching, Generated, Elsewhere, FilePath, TypesOnly);

		public ArgumentException Ambiguous() => DeclarationLocator.Ambiguous(Resolution.Address, Declarations, FilePath);
	}

	/// <summary>
	/// Nothing matched, and the reasons why are worth telling apart: the name exists nowhere, it
	/// exists somewhere other than where the caller said, it exists only in generated code, or it
	/// exists but not in the file the caller pinned it to.
	/// <para>
	/// A refusal meaning "source declares nothing this address reaches" is a
	/// <see cref="SymbolNotFoundException"/>, whatever it goes on to say, because that is the
	/// condition under which a read answers from metadata instead -- and a read that has already
	/// asked says so, so a caller can tell "not in your source" from "not anywhere". Carrying the name
	/// somewhere is not the same as being reached by the address: a solution of any size declares an
	/// Add, a Name and a Document of its own, and none of them is what System.Collections.Generic.List.Add
	/// names.
	/// </para>
	/// <para>
	/// The rest are deliberately not that type, and each for the same reason: source did reach
	/// something, so a referenced assembly has nothing to add and answering from one would be an
	/// answer about a different symbol. A declaration ruled out by where it lives was still found,
	/// a type reached by a constructor address is the type the caller meant whatever constructors it
	/// declares, and a name that turned out to be a method rather than a type is a question about this
	/// solution whichever way it is answered.
	/// </para>
	/// </summary>
	private static ArgumentException NotFound(
		SymbolResolution resolution,
		IReadOnlyList<ISymbol> matching,
		int generated,
		int elsewhere,
		string? filePath,
		bool typesOnly)
	{
		var address = resolution.Address;

		var metadata = resolution.MetadataSearched
			? " Nothing in a referenced assembly is declared there either."
			: string.Empty;

		if (resolution.Source.Count == 0 && resolution.Named.Count == 0)
		{
			return new SymbolNotFoundException(
				$"Nothing in the solution is called {Quote(address.Name)}.{metadata} Ask rose_search_symbols, which "
					+ "matches names by pattern and by abbreviation and returns the qualified name this argument "
					+ "wants. For a type in a referenced assembly, rose_resolve_name searches metadata as well as source.");
		}

		if (resolution.Source.Count == 0 && address.Constructor != ConstructorKind.None)
		{
			return NoConstructor(address, resolution.Constructed, metadata);
		}

		if (resolution.Source.Count == 0)
		{
			var qualified = resolution.Named
				.Where(symbol => !typesOnly || symbol is INamedTypeSymbol)
				.Select(symbol => string.Join(".", SymbolAddress.PathOf(symbol)))
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal)
				.ToArray();

			var overloads = address.Parameters is null
				? string.Empty
				: " No overload takes those parameter types; leave the parameter list off to be told what there is.";

			var declaredAs = qualified.Length == 0
				? string.Empty
				: $" {Quote(address.Name)} is declared as {Summarise(qualified)}.";

			return new SymbolNotFoundException(
				$"Nothing is declared at {Quote(address.Requested)}.{declaredAs}{overloads}{metadata}");
		}

		if (matching.Count == 0)
		{
			return new ArgumentException(
				$"{Quote(address.Requested)} is a {Kind(resolution.Source[0])}, not a type. Only a type has members to add to.");
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

	/// <summary>
	/// Why a constructor address found nothing, which is three different situations wearing one
	/// error. The type may not be there at all; it may be there with a constructor the compiler
	/// wrote, which is not in the file and cannot be edited; or it may declare constructors that
	/// take other parameters.
	/// <para>
	/// Only the first of those may be answered from metadata, and the distinction is the whole
	/// reason it carries its own type. Once a type is declared here at the path the caller wrote, the
	/// caller means that type: answering about a referenced assembly's Greeter because this
	/// solution's Greeter leaves its constructor to the compiler would be a complete, well-formed
	/// answer about somebody else's class. A type of that name declared at some other path is not
	/// that, and does not stop a library's constructor being reached.
	/// </para>
	/// <para>
	/// Where the address could also be read as a type, the refusal says that reading found nothing
	/// too, so the advice to add a constructor is never given to a caller who meant a type.
	/// </para>
	/// </summary>
	private static ArgumentException NoConstructor(
		SymbolAddress address,
		IReadOnlyList<INamedTypeSymbol> types,
		string metadata)
	{
		var asType = address.AsType is { } other
			? $" Read as a type instead, {Quote(other.Requested)} names nothing either: no type {Quote(other.Name)} is "
				+ $"declared in a namespace or type ending {Quote(string.Join(".", other.Path.Take(other.Path.Count - 1)))}."
			: string.Empty;

		if (types.Count == 0)
		{
			return new SymbolNotFoundException(
				$"No type is declared at {Quote(string.Join(".", address.Path))} in this solution, so "
					+ $"{Quote(address.Requested)} names no constructor.{asType}{metadata}");
		}

		if (address.Constructor == ConstructorKind.Static)
		{
			return new ArgumentException($"{Quote(address.Name)} declares no static constructor.");
		}

		var declared = types.SelectMany(type => type.Constructors)
			.Where(constructor => !constructor.IsImplicitlyDeclared)
			.ToArray();

		if (declared.Length == 0)
		{
			return new ArgumentException(
				$"{Quote(address.Name)} declares no constructor. The parameterless one it has is written by the "
					+ $"compiler rather than by the file, so there is nothing here to change.{asType} To give it "
					+ "a constructor, add one with rose_add_member.");
		}

		var signatures = declared
			.Select(constructor => constructor.ToDisplayString(SymbolSignature.Format))
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		return new ArgumentException(
			$"No constructor of {Quote(address.Name)} takes those parameter types. It declares "
				+ $"{Summarise(signatures)}.{asType}");
	}

	/// <summary>
	/// A positional record property is declared by a parameter, so the only declaration there is to
	/// write over in its name is the whole record -- which is not what a caller naming the property
	/// meant. The refusal names the tools that do reach it.
	/// </summary>
	private static ArgumentException Positional(DeclarationTarget target)
	{
		var record = target.Symbol.ContainingType is { } type ? SymbolAddress.Of(type) : null;

		return new ArgumentException(
			$"{Quote(SymbolAddress.Of(target.Symbol))} is a positional property, declared by a parameter of "
				+ $"{Quote(record)} rather than as a member of its own, so there is no declaration of it to write "
				+ "over. rose_rename_symbol renames it; rose_change_signature on the record's constructor changes "
				+ "its type or removes it; rose_add_member can declare it explicitly instead.");
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

		// A type and its constructor, which is the one ambiguity a repeated last segment brings: the
		// way out is a spelling that only one of the readings accepts.
		var typeAndConstructor = candidates.Any(candidate => candidate.Symbol is INamedTypeSymbol)
			&& candidates.Any(candidate => candidate.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor });

		// One symbol in several places is a partial, which no parameter list can separate however
		// precisely it is written. Several symbols are overloads, which one can.
		var how = typeAndConstructor
			? $"It reads both as a type and as that type's constructor. For the constructor write "
				+ $"{string.Join(".", address.Path)}..ctor, or give its parameter types; for the type, name it with the "
				+ "whole of its namespace, or pass filePath."
			: !separateSymbols
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
