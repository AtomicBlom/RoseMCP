using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// A declaration's accessibility: read from what a caller wrote, checked against what the language
/// allows where the declaration sits, and written onto the declaration.
/// <para>
/// Only the accessibility keywords move. Everything else in the modifier list -- static, sealed,
/// override, async -- is the member's own and stays where it is, and so does whatever sat in front of
/// the first keyword: a documentation comment and the line's indentation are leading trivia on
/// whichever token comes first, and that token is the one this replaces or puts something in front of.
/// </para>
/// </summary>
public static class AccessibilityModifiers
{
	/// <summary>Every keyword that says who may see a declaration, file included, so a file-local type can be widened.</summary>
	private static readonly SyntaxKind[] Keywords =
	[
		SyntaxKind.PublicKeyword,
		SyntaxKind.PrivateKeyword,
		SyntaxKind.ProtectedKeyword,
		SyntaxKind.InternalKeyword,
		SyntaxKind.FileKeyword,
	];

	/// <summary>
	/// The accessibility a caller wrote, as the compiler names it. The two-word forms are accepted
	/// either way round, since both compile.
	/// </summary>
	public static Accessibility Parse(string written)
	{
		var words = written
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Select(word => word.ToLowerInvariant())
			.Order(StringComparer.Ordinal);

		return string.Join(" ", words) switch
		{
			"public" => Accessibility.Public,
			"internal" => Accessibility.Internal,
			"protected" => Accessibility.Protected,
			"private" => Accessibility.Private,
			"internal protected" => Accessibility.ProtectedOrInternal,
			"private protected" => Accessibility.ProtectedAndInternal,
			_ => throw new ArgumentException(
				$"'{written.Trim()}' is not an accessibility. Write one of public, internal, protected, private, "
					+ "protected internal or private protected."),
		};
	}

	/// <summary>The keywords that spell an accessibility, in the order the language's own style writes them.</summary>
	public static IReadOnlyList<SyntaxKind> KeywordsFor(Accessibility accessibility) => accessibility switch
	{
		Accessibility.Public => [SyntaxKind.PublicKeyword],
		Accessibility.Internal => [SyntaxKind.InternalKeyword],
		Accessibility.Protected => [SyntaxKind.ProtectedKeyword],
		Accessibility.Private => [SyntaxKind.PrivateKeyword],
		Accessibility.ProtectedOrInternal => [SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword],
		Accessibility.ProtectedAndInternal => [SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword],
		_ => throw new ArgumentOutOfRangeException(nameof(accessibility), accessibility, "Not an accessibility C# can write."),
	};

	/// <summary>An accessibility as the language spells it, or null for one it has no keywords for.</summary>
	public static string? Spelled(Accessibility accessibility) =>
		accessibility is Accessibility.NotApplicable
			? null
			: string.Join(" ", KeywordsFor(accessibility).Select(SyntaxFacts.GetText));

	/// <summary>
	/// Why the language will not let <paramref name="symbol"/> have <paramref name="wanted"/>, or null
	/// where it will. Asked before anything is written, so a refusal costs nothing.
	/// <para>
	/// Each of these compiles to an error that names the declaration rather than the request, so a
	/// caller who wrote it would be told about a line they never asked to change. The ones left to the
	/// compile afterwards are the ones about other code: what can no longer see the member, and what
	/// it now exposes that is less accessible than itself.
	/// </para>
	/// </summary>
	public static string? WhyRefused(ISymbol symbol, MemberDeclarationSyntax declaration, Accessibility wanted)
	{
		var name = SymbolSignature.Of(symbol);
		var restricted = wanted is Accessibility.Protected or Accessibility.ProtectedOrInternal or Accessibility.ProtectedAndInternal;

		if (symbol is IMethodSymbol { MethodKind: MethodKind.StaticConstructor or MethodKind.Destructor })
		{
			return $"{name} is called by the runtime rather than by code, and the language gives it no accessibility to change.";
		}

		if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && declaration is TypeDeclarationSyntax)
		{
			return $"{name} is a primary constructor, written on the type, and the language gives it no accessibility of its own. "
				+ "Name the type to change the type's.";
		}

		if (declaration is EnumMemberDeclarationSyntax)
		{
			return $"{name} is an enum value, which is exactly as accessible as its enum. Name the enum to change that.";
		}

		if (ExplicitlyImplemented(symbol) is { } implemented)
		{
			return $"{name} implements {SymbolSignature.Of(implemented)} explicitly, which the language allows no accessibility on: "
				+ "it is reachable only through the interface.";
		}

		if (declaration is BaseFieldDeclarationSyntax { Declaration.Variables.Count: > 1 } field)
		{
			var others = field.Declaration.Variables
				.Select(variable => variable.Identifier.Text)
				.Where(other => !string.Equals(other, symbol.Name, StringComparison.Ordinal));

			return $"{symbol.Name} shares its declaration with {string.Join(", ", others)}, so changing its accessibility would "
				+ "change theirs too. Give them declarations of their own first.";
		}

		if (symbol is INamedTypeSymbol && symbol.ContainingType is null && wanted is not (Accessibility.Public or Accessibility.Internal))
		{
			return $"{name} is not nested in a type, so it can only be public or internal: anything else is CS1527.";
		}

		if (restricted && symbol.ContainingType is { TypeKind: TypeKind.Struct })
		{
			return $"{name} is declared in a struct, which nothing can derive from, so protected is CS0666.";
		}

		if (restricted && symbol.ContainingType is { IsStatic: true })
		{
			return $"{name} is declared in a static class, which nothing can derive from, so protected is CS1057.";
		}

		for (var above = Overridden(symbol); above is not null; above = Overridden(above))
		{
			if (above.Locations.Any(location => location.IsInSource)) continue;

			return $"{name} overrides {SymbolSignature.Of(above)}, which a referenced assembly declares, and an override has to "
				+ "keep the accessibility of what it overrides: anything else is CS0507.";
		}

		return null;
	}

	/// <summary>What a member overrides, whichever kind of member it is.</summary>
	public static ISymbol? Overridden(ISymbol symbol) => symbol switch
	{
		IMethodSymbol method => method.OverriddenMethod,
		IPropertySymbol property => property.OverriddenProperty,
		IEventSymbol @event => @event.OverriddenEvent,
		_ => null,
	};

	/// <summary>
	/// The interface member <paramref name="symbol"/> implements by matching it, or null. Matching is
	/// the half that depends on accessibility: a member stops implementing the interface the moment it
	/// is not public, and nothing at the member says so.
	/// </summary>
	public static ISymbol? ImplicitlyImplemented(ISymbol symbol)
	{
		if (symbol.ContainingType is not { TypeKind: not TypeKind.Interface } type) return null;
		if (ExplicitlyImplemented(symbol) is not null) return null;

		return type.AllInterfaces
			.SelectMany(@interface => @interface.GetMembers(symbol.Name))
			.FirstOrDefault(member => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol));
	}

	/// <summary>
	/// Where the accessibility keywords are, or the empty place they would go where there are none,
	/// which is what changing them asks to change.
	/// </summary>
	public static TextSpan SpanOf(MemberDeclarationSyntax declaration)
	{
		var present = declaration.Modifiers.Where(IsAccessibility).ToArray();

		return present.Length > 0
			? TextSpan.FromBounds(present[0].SpanStart, present[^1].Span.End)
			: new TextSpan(FirstAfterAttributes(declaration).SpanStart, 0);
	}

	/// <summary>
	/// The declaration with its accessibility keywords replaced by <paramref name="keywords"/>, each
	/// annotated with <paramref name="marker"/>. They go where the old ones were, so a file that writes
	/// <c>static public</c> keeps its order; with none to replace they go first, taking the leading
	/// trivia of the token that was first.
	/// </summary>
	public static MemberDeclarationSyntax With(
		MemberDeclarationSyntax declaration,
		IReadOnlyList<SyntaxKind> keywords,
		SyntaxAnnotation marker)
	{
		var modifiers = declaration.Modifiers;
		var at = -1;

		for (var index = 0; index < modifiers.Count; index++)
		{
			if (!IsAccessibility(modifiers[index])) continue;

			at = index;
			break;
		}

		if (at < 0)
		{
			var first = FirstAfterAttributes(declaration);
			var stripped = declaration.ReplaceToken(first, first.WithLeadingTrivia());
			var inserted = Tokens(keywords, first.LeadingTrivia, SyntaxFactory.TriviaList(SyntaxFactory.Space), marker);

			return stripped.WithModifiers(stripped.Modifiers.InsertRange(0, inserted));
		}

		var replaced = modifiers.Where(IsAccessibility).ToArray();
		var written = Tokens(keywords, replaced[0].LeadingTrivia, replaced[^1].TrailingTrivia, marker);

		return declaration.WithModifiers(SyntaxFactory.TokenList(
			[
				.. modifiers.Take(at),
				.. written,
				.. modifiers.Skip(at).Where(token => !IsAccessibility(token)),
			]));
	}

	private static bool IsAccessibility(SyntaxToken token) => Keywords.Contains(token.Kind());

	/// <summary>
	/// The first token after the attributes, which is where an accessibility keyword goes: the
	/// documentation comment above a member without attributes is leading trivia on this token.
	/// </summary>
	private static SyntaxToken FirstAfterAttributes(MemberDeclarationSyntax declaration) =>
		declaration.AttributeLists.Count > 0
			? declaration.AttributeLists[^1].GetLastToken().GetNextToken()
			: declaration.GetFirstToken();

	private static IEnumerable<SyntaxToken> Tokens(
		IReadOnlyList<SyntaxKind> keywords,
		SyntaxTriviaList leading,
		SyntaxTriviaList trailing,
		SyntaxAnnotation marker) =>
		keywords.Select((kind, index) => SyntaxFactory
			.Token(
				index == 0 ? leading : SyntaxFactory.TriviaList(),
				kind,
				index == keywords.Count - 1 ? trailing : SyntaxFactory.TriviaList(SyntaxFactory.Space))
			.WithAdditionalAnnotations(marker));

	/// <summary>The interface member a member implements explicitly, or null where it implements none that way.</summary>
	private static ISymbol? ExplicitlyImplemented(ISymbol symbol) => symbol switch
	{
		IMethodSymbol method => method.ExplicitInterfaceImplementations.FirstOrDefault(),
		IPropertySymbol property => property.ExplicitInterfaceImplementations.FirstOrDefault(),
		IEventSymbol @event => @event.ExplicitInterfaceImplementations.FirstOrDefault(),
		_ => null,
	};
}
