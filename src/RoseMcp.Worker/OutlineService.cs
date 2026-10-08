using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// What a type or a file declares, listed rather than read.
/// <para>
/// One tool with two roots, because the result is the same shape either way and only the starting
/// point differs: a name when the caller knows the type, a path when they have the file. Two tools
/// would be two descriptions saying the same thing and two slots on every client's list.
/// </para>
/// <para>
/// This is the read that comes before an edit, and it had no tool. Asking what a type contains
/// meant reading the file, and reading the file is what puts it in front of the caller -- after
/// which the edit goes through a text tool and none of the rest of this surface is worth reaching
/// for.
/// </para>
/// <para>
/// The answer is cheap by default and grows only on request, because the type an outline is worth
/// most on is a large one, and that is where a full answer overruns what a client will accept. A
/// member is its name, kind, accessibility and line; what is the same for every member is said once
/// on the type; and a caller who wants a signature or the prose has just been told the names and can
/// narrow to the members it cares about.
/// </para>
/// </summary>
public static class OutlineService
{
	/// <summary>
	/// How many members an outline lists when the caller does not say. The same as rose_find_references'
	/// cap, and comfortably under a client's limit at the default detail.
	/// </summary>
	public const int DefaultMaxMembers = 200;

	public static async Task<OutlineResult> OutlineAsync(
		WorkspaceSnapshot snapshot,
		string? type,
		string? filePath,
		bool includeInherited,
		bool includeDocumentation,
		bool includeSignatures,
		CancellationToken cancellationToken,
		string? members = null,
		int maxMembers = DefaultMaxMembers)
	{
		var named = !string.IsNullOrWhiteSpace(type);
		var pathed = !string.IsNullOrWhiteSpace(filePath);

		if (named == pathed)
		{
			throw new ArgumentException(
				named
					? "Name a type or give a file path, not both -- they are two ways of choosing what to outline."
					: "Name a type, as Namespace.Type, or give a file path.");
		}

		var notices = new List<string>(snapshot.Notices);

		var filter = string.IsNullOrWhiteSpace(members) ? null : members.Trim();
		var listing = new Listing(maxMembers <= 0 ? DefaultMaxMembers : maxMembers);
		var detail = new OutlineDetail(includeDocumentation, includeSignatures, includeInherited, filter);

		var types = named
			? [await OfTypeAsync(snapshot, type!, filePath, detail, listing, cancellationToken)]
			: await OfFileAsync(snapshot, filePath!, detail, listing, cancellationToken);

		if (types.Count == 0) notices.Add("The file declares no types.");

		var found = types.Sum(outlined => outlined.TotalMembers);
		if (types.Count > 0) notices.AddRange(listing.Notices(filter, found));

		return new OutlineResult
		{
			Revision = snapshot.Revision,
			Target = (named ? type : filePath)!,
			Types = types,
			Truncated = listing.Truncated,
			Notices = notices,
		};
	}

	/// <summary>
	/// The members of a type from a referenced assembly that code outside that assembly can use: its
	/// own public, protected and protected internal members, each with its signature and whether it is
	/// obsolete, narrowed and capped the way an outline is.
	/// <para>
	/// Signatures are always given, unlike an outline's default, because a metadata member has no line:
	/// overloads would otherwise be the same name listed several times, and which ones exist is the
	/// question. Inherited members are left out, as an outline leaves them out by default; each base type
	/// can be asked about in turn.
	/// </para>
	/// </summary>
	/// <param name="snapshot">The solution the type was resolved in.</param>
	/// <param name="type">The type, from metadata.</param>
	/// <param name="members">Only members whose name contains this, ignoring case; null for all.</param>
	/// <param name="maxMembers">How many to list at most; zero or less means <see cref="DefaultMaxMembers"/>.</param>
	/// <param name="cancellationToken">Cancels the listing.</param>
	public static MemberListing ListReachable(
		WorkspaceSnapshot snapshot,
		INamedTypeSymbol type,
		string? members,
		int maxMembers,
		CancellationToken cancellationToken)
	{
		var filter = string.IsNullOrWhiteSpace(members) ? null : members.Trim();
		var listing = new Listing(maxMembers <= 0 ? DefaultMaxMembers : maxMembers);
		var detail = new OutlineDetail(Documentation: false, Signatures: true, Inherited: false, filter);

		var all = Members(type, includeInherited: false).Where(IsReachableFromOutside).ToList();
		var matching = all.Where(detail.Matches).ToList();

		listing.Unfiltered = all.Count;

		var listed = new List<OutlinedMember>();

		foreach (var member in listing.Take(matching))
		{
			cancellationToken.ThrowIfCancellationRequested();

			listed.Add(DescribeMember(snapshot, type, member, home: null, typeSummary: null, detail, cancellationToken));
		}

		return new MemberListing
		{
			Members = listed,
			Total = matching.Count,
			Truncated = listing.Truncated,
			Notices = [.. listing.Notices(filter, matching.Count)],
		};
	}

	/// <summary>
	/// What to put in each entry and which members to list. Documentation and signatures are two
	/// switches rather than one, because they answer different questions: documentation is what a
	/// member is for, a signature is what it takes and returns, and a caller looking for a name in a
	/// large type wants neither.
	/// <para>
	/// A record rather than parameters threaded through four methods, so another switch does not mean
	/// another parameter on every one of them.
	/// </para>
	/// </summary>
	private readonly record struct OutlineDetail(bool Documentation, bool Signatures, bool Inherited, string? NameFilter)
	{
		public bool Matches(ISymbol member) =>
			NameFilter is null || member.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The member cap, shared across every type in the answer: a file of several types is one answer,
	/// and a cap per type would let a file of many small types overrun it anyway.
	/// </summary>
	private sealed class Listing(int cap)
	{
		public int Cap { get; } = cap;

		public int Listed { get; private set; }

		/// <summary>How many members the types have before the name filter, for the notice a filter matching none of them needs.</summary>
		public int Unfiltered { get; set; }

		public bool Truncated { get; private set; }

		/// <summary>As many of these as the cap still has room for, recording that it cut some off.</summary>
		public IReadOnlyList<ISymbol> Take(IReadOnlyList<ISymbol> matching)
		{
			var room = Math.Max(0, Cap - Listed);
			if (matching.Count > room) Truncated = true;

			var taken = matching.Take(room).ToList();
			Listed += taken.Count;

			return taken;
		}

		/// <summary>
		/// What the narrowing left out, said, since a short or empty list otherwise reads as the whole
		/// answer: a name filter that matched nothing, and a cap that stopped the listing.
		/// </summary>
		/// <param name="filter">The name filter, where the caller gave one.</param>
		/// <param name="found">How many members matched, listed or not.</param>
		public IEnumerable<string> Notices(string? filter, int found)
		{
			var nothingMatched = filter is not null && found == 0;

			if (nothingMatched)
			{
				yield return Unfiltered == 0
					? $"No member's name contains '{filter}': there are no members to match."
					: $"No member's name contains '{filter}', so none is listed. Leave members off to list all {Plural(Unfiltered, "member")}.";
			}

			if (Truncated)
			{
				yield return $"Listed {Listed} of {Plural(found, "member")}, stopping at maxMembers={Cap}. "
					+ "Narrow by name with members, or raise maxMembers, for the rest.";
			}
		}
	}

	private static async Task<OutlinedType> OfTypeAsync(
		WorkspaceSnapshot snapshot,
		string type,
		string? filePath,
		OutlineDetail detail,
		Listing listing,
		CancellationToken cancellationToken)
	{
		var target = await DeclarationLocator.FindTypeAsync(snapshot.Solution, type, filePath, cancellationToken);

		return await DescribeAsync(snapshot, target.Symbol, target.Declaration.SyntaxTree, detail, listing, cancellationToken);
	}

	private static async Task<IReadOnlyList<OutlinedType>> OfFileAsync(
		WorkspaceSnapshot snapshot,
		string filePath,
		OutlineDetail detail,
		Listing listing,
		CancellationToken cancellationToken)
	{
		var document = SymbolLocator.RequireDocument(snapshot.Solution, filePath);

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var root = await document.GetSyntaxRootAsync(cancellationToken);

		if (model is null || root is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");
		}

		var described = new List<OutlinedType>();
		var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

		// Declared order, not alphabetical: a file's own order is what a reader is going to see when
		// they open it, and sorting would make the outline and the file disagree.
		foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol) continue;

			// A partial type declared in two blocks of one file is one type: listing it per block would
			// spend the member cap on it twice and count its members twice in the totals.
			if (!seen.Add(symbol)) continue;

			described.Add(await DescribeAsync(snapshot, symbol, root.SyntaxTree, detail, listing, cancellationToken));
		}

		return described;
	}

	/// <summary>
	/// One type. <paramref name="home"/> is the file its members are reported against: a member
	/// declared there gives only its line, and one declared anywhere else names its file too.
	/// </summary>
	private static async Task<OutlinedType> DescribeAsync(
		WorkspaceSnapshot snapshot,
		INamedTypeSymbol symbol,
		SyntaxTree home,
		OutlineDetail detail,
		Listing listing,
		CancellationToken cancellationToken)
	{
		var all = Members(symbol, detail.Inherited).ToList();
		var matching = all.Where(detail.Matches).ToList();

		listing.Unfiltered += all.Count;

		var summary = detail.Documentation ? Summary(symbol, cancellationToken) : null;
		var members = new List<OutlinedMember>();

		foreach (var member in listing.Take(matching))
		{
			cancellationToken.ThrowIfCancellationRequested();

			members.Add(DescribeMember(snapshot, symbol, member, home, summary, detail, cancellationToken));
		}

		var bases = new List<string>();

		if (symbol.BaseType is { SpecialType: not SpecialType.System_Object } super)
		{
			bases.Add(super.ToDisplayString(SymbolSignature.Format));
		}

		bases.AddRange(symbol.Interfaces.Select(@interface => @interface.ToDisplayString(SymbolSignature.Format)));

		var declarations = new List<SourceLocation>();

		foreach (var location in symbol.Locations.Where(location => location.IsInSource))
		{
			// The member a type's declaration sits inside is the type itself, already named above.
			var described = await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken);
			declarations.Add(described with { ContainingMember = null });
		}

		return new OutlinedType
		{
			Name = symbol.ToDisplayString(SymbolSignature.Format),
			Kind = Kind(symbol),
			Accessibility = symbol.DeclaredAccessibility.ToString(),
			Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false
				? symbol.ContainingNamespace.ToDisplayString()
				: null,
			BaseTypes = bases,
			Summary = summary,
			FilePath = home.FilePath,
			Declarations = declarations,
			TotalMembers = matching.Count,
			Members = members,
		};
	}

	/// <summary>
	/// One member. <paramref name="home"/> is the file its type is reported against, and null for a type
	/// from a referenced assembly, whose members have no file and so no line either.
	/// </summary>
	private static OutlinedMember DescribeMember(
		WorkspaceSnapshot snapshot,
		INamedTypeSymbol type,
		ISymbol member,
		SyntaxTree? home,
		string? typeSummary,
		OutlineDetail detail,
		CancellationToken cancellationToken)
	{
		// The declaration in the type's own file where a partial member has one there, so a member
		// split across files is reported where the caller is already looking.
		var location = member.Locations.FirstOrDefault(candidate => home is not null && candidate.SourceTree == home)
			?? member.Locations.FirstOrDefault(candidate => candidate.IsInSource);

		var span = location?.GetLineSpan();
		var elsewhere = location is not null && location.SourceTree != home;

		var inherited = !SymbolEqualityComparer.Default.Equals(member.ContainingType, type);

		return new OutlinedMember
		{
			Name = member.Name,
			Kind = member.Kind.ToString(),
			Line = span?.StartLinePosition.Line + 1,
			FilePath = elsewhere ? span?.Path : null,
			Accessibility = member.DeclaredAccessibility.ToString(),
			Signature = detail.Signatures ? member.ToDisplayString(SymbolSignature.Format) : null,
			IsAbstract = member.IsAbstract,
			IsStatic = member.IsStatic,

			// A generator's member has no file to edit, and saying so here saves a call that would
			// refuse for exactly that reason.
			IsGenerated = member.DeclaringSyntaxReferences.Length > 0
				&& member.DeclaringSyntaxReferences.All(reference =>
					snapshot.Solution.GetDocument(reference.SyntaxTree) is not { } owner
							|| GeneratedCode.Is(owner, reference.GetSyntax(cancellationToken).FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? reference.GetSyntax(cancellationToken))),
			Obsolete = Obsolescence(member),
			DeclaringType = inherited ? member.ContainingType?.ToDisplayString(SymbolSignature.Format) : null,
			Summary = detail.Documentation ? MemberSummary(member, typeSummary, cancellationToken) : null,
		};
	}

	/// <summary>
	/// <c>warning</c> or <c>error</c> where the member carries <c>[Obsolete]</c>, and null otherwise.
	/// The marks a compiler writes for older compilers -- on a constructor of a type with required
	/// members, or a ref struct -- are not among the attributes Roslyn reads back, so what is left is
	/// what a current build acts on.
	/// </summary>
	private static string? Obsolescence(ISymbol member)
	{
		var obsolete = member.GetAttributes().FirstOrDefault(attribute =>
			attribute.AttributeClass is
			{
				Name: nameof(ObsoleteAttribute),
				ContainingType: null,
				ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
			});

		if (obsolete is null) return null;

		var isError = obsolete.ConstructorArguments is [_, { Value: true }];

		return isError ? "error" : "warning";
	}

	/// <summary>
	/// Whether code outside the type's assembly can use the member: public, protected or protected
	/// internal, and named something it can write. Metadata carries members a compiler emitted under
	/// names no source can spell -- a record's <c>&lt;Clone&gt;$</c>, a lambda's closure class -- which
	/// are public and still not part of anything a caller can call. A constructor and an indexer are
	/// the two a caller writes without their metadata name, <c>.ctor</c> and <c>this[]</c>, so neither
	/// is held to it. The compiler's <c>[CompilerGenerated]</c> is not the test, because a record's
	/// <c>Equals</c>, <c>ToString</c> and <c>Deconstruct</c> carry it too and are called like any other
	/// member.
	/// </summary>
	private static bool IsReachableFromOutside(ISymbol member)
	{
		var accessible = member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
		var writtenWithoutItsName = member is IMethodSymbol { MethodKind: MethodKind.Constructor } or IPropertySymbol { IsIndexer: true };
		var speakable = writtenWithoutItsName || SyntaxFacts.IsValidIdentifier(member.Name);

		return accessible && speakable;
	}

	/// <summary>
	/// A member's summary, less the one a constructor inherits from its type: a primary constructor is
	/// documented by its type's comment, and repeating the class summary on the constructor pays for
	/// the longest paragraph in the answer twice.
	/// </summary>
	private static string? MemberSummary(ISymbol member, string? typeSummary, CancellationToken cancellationToken)
	{
		var summary = Summary(member, cancellationToken);

		var repeatsTheType = member is IMethodSymbol { MethodKind: MethodKind.Constructor }
			&& string.Equals(summary, typeSummary, StringComparison.Ordinal);

		return repeatsTheType ? null : summary;
	}

	/// <summary>
	/// The members worth listing. Compiler-written ones are left out -- a property's backing field
	/// and its get and set accessors are not things anybody asks a type what it contains and would
	/// treble the length of the answer.
	/// </summary>
	private static IEnumerable<ISymbol> Members(INamedTypeSymbol symbol, bool includeInherited)
	{
		var own = symbol.GetMembers().Where(member => !member.IsImplicitlyDeclared && member.Kind != SymbolKind.Method
			|| member is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion }
				&& !member.IsImplicitlyDeclared);

		if (!includeInherited) return own;

		var inherited = Bases(symbol)
			.SelectMany(super => super.GetMembers())
			.Where(member => !member.IsImplicitlyDeclared && member.DeclaredAccessibility != Accessibility.Private);

		return own.Concat(inherited);
	}

	private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol symbol)
	{
		for (var super = symbol.BaseType; super is { SpecialType: not SpecialType.System_Object }; super = super.BaseType)
		{
			yield return super;
		}
	}

	private static string Kind(INamedTypeSymbol symbol) => symbol.TypeKind switch
	{
		TypeKind.Interface => "interface",
		TypeKind.Struct => symbol.IsRecord ? "record struct" : "struct",
		TypeKind.Enum => "enum",
		TypeKind.Delegate => "delegate",
		_ => symbol.IsRecord ? "record" : "class",
	};

	private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

	/// <summary>
	/// The first sentence of the summary, rendered. One sentence because an outline is a list and a
	/// paragraph per member is not a list -- on a well-documented type the summaries would be most of the
	/// answer -- and the whole summary is a rose_symbol_info call away for a member that turns out to
	/// matter.
	/// </summary>
	private static string? Summary(ISymbol symbol, CancellationToken cancellationToken) =>
		DocumentationText.FirstSentence(symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken));
}
